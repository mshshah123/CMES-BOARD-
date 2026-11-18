using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using System.Data;

namespace CMES.Controllers
{
    // Model Wise Tracking — engines grouped by PRODUCTNO (model) x location bucket.
    // Faithful port of sir's Oracle query (COB_T_SERIAL_NO / PRODUCT), adapted to
    // the local SQL Server mirror (MPI_COB_T_SERIAL_NO / MPI_PRODUCT) the same way
    // WipController was.
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Policy = "CmesUser")]
    public class ModelTrackingController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly IMemoryCache   _cache;
        private readonly ILogger<ModelTrackingController> _logger;

        // Unfiltered (all-models) summary is a full-table scan+pivot — cache it.
        private const string CacheKey = "modeltracking_summary_all";

        // ── Base filter ───────────────────────────────────────────────────────
        // Same source table/columns as WipController, but STATUS also includes 3
        // (FES) — Model Wise Tracking tracks FES-completed engines too, unlike
        // the plain WIP Report which only cares about 1/2/6.
        private const string BaseSql = @"
    dbo.MPI_COB_T_SERIAL_NO C
    INNER JOIN dbo.MPI_PRODUCT P ON P.ID = C.PRODUCTID
    WHERE  C.STATUS   IN (1, 2, 3, 6)
    AND LEN(C.SERIALNO) = 8
    AND C.CREATEDON    >= '2025-08-01'";

        // ── Location bucket ──────────────────────────────────────────────────
        // A coarser, Model-Tracking-specific CASE than WipController.DeriveLocationSql —
        // OLD LINE/NEW LINE/LINESET all collapse into a single 'WIP' bucket here
        // (sir's original query only ever shows one "WIP" column), and FES
        // (STATUS=3) is its own bucket, checked first.
        private const string LocationSql = @"
CASE
    WHEN C.STATUS = 3                 THEN 'FES'
    WHEN C.LOCATION = 'BLB REPAIR'    THEN 'TEST REWORK'
    WHEN C.LOCATION = 'PART SHORTAGE' THEN 'SHORT BUILD'
    WHEN C.LOCATION IS NOT NULL       THEN C.LOCATION
    WHEN (C.WORKSTATION BETWEEN '10000' AND '33400')
      OR C.WORKSTATION = 'TC1CMW101MINIE1'
      OR C.LOCATION = 'ATP REPAIR'
        THEN 'WIP'
    WHEN C.WORKSTATION BETWEEN '40000' AND '44600' THEN 'TEST CELL LINE'
    WHEN C.WORKSTATION BETWEEN '50000' AND '51905' THEN 'PAINT LINE'
    WHEN (C.LOCATION = 'PAINT REPAIR' OR C.WORKSTATION = '54000')
        THEN 'PAINT REPAIR'
    WHEN (C.LOCATION = 'QUALITY DOCK' OR C.WORKSTATION IN ('52000','55000','52100','52200'))
        THEN 'QUALITY DOCK'
    WHEN (C.LOCATION = 'MRA' OR C.WORKSTATION = '34000')
        THEN 'MRA'
    WHEN C.LOCATION = 'PE'        THEN 'PE'
    WHEN C.LOCATION = 'EQA AUDIT' THEN 'EQA AUDIT'
    ELSE 'UNKNOWN'
END";

        public ModelTrackingController(IConfiguration configuration, IMemoryCache cache, ILogger<ModelTrackingController> logger)
        {
            _configuration = configuration;
            _cache = cache;
            _logger = logger;
        }

        // ══════════════════════════════════════════════════════════════════════
        // GET /api/modeltracking/summary[?modelNo=SO10205]
        // No modelNo -> full pivot for every model (cached 5 min).
        // With modelNo -> just that model's row (not cached, cheap targeted query).
        // ══════════════════════════════════════════════════════════════════════
        [HttpGet("summary")]
        public async Task<IActionResult> GetSummary([FromQuery] string? modelNo, CancellationToken ct)
        {
            var trimmed = string.IsNullOrWhiteSpace(modelNo) ? null : modelNo.Trim();

            if (trimmed == null && _cache.TryGetValue(CacheKey, out List<ModelSummaryRow>? cached) && cached != null)
            {
                _logger.LogInformation("[ModelTrackingController] GetSummary: cache hit ({Count} models).", cached.Count);
                return Ok(cached);
            }

            try
            {
                var rows = await FetchSummaryAsync(trimmed, ct);

                if (trimmed == null)
                    _cache.Set(CacheKey, rows, TimeSpan.FromMinutes(5));

                _logger.LogInformation("[ModelTrackingController] GetSummary: returning {Count} model(s) (modelNo={ModelNo}).",
                    rows.Count, trimmed ?? "(all)");
                return Ok(rows);
            }
            catch (SqlException ex)
            {
                _logger.LogError(ex, "[ModelTrackingController] GetSummary SQL error.");
                return StatusCode(500, new { message = "Unable to load model tracking summary.", detail = ex.Message });
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // GET /api/modeltracking/details?modelNo=SO10205
        // ══════════════════════════════════════════════════════════════════════
        [HttpGet("details")]
        public async Task<IActionResult> GetDetails([FromQuery] string modelNo, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(modelNo))
                return BadRequest(new { message = "modelNo is required." });

            try
            {
                var rows = await FetchDetailsAsync(modelNo.Trim(), ct);
                _logger.LogInformation("[ModelTrackingController] GetDetails: returning {Count} rows for model '{ModelNo}'.",
                    rows.Count, modelNo);
                return Ok(rows);
            }
            catch (SqlException ex)
            {
                _logger.LogError(ex, "[ModelTrackingController] GetDetails SQL error for model '{ModelNo}'.", modelNo);
                return StatusCode(500, new { message = "Unable to load model details.", detail = ex.Message });
            }
        }

        // ── Private helpers ──────────────────────────────────────────────────

        private async Task<List<ModelSummaryRow>> FetchSummaryAsync(string? modelNo, CancellationToken ct)
        {
            var connectionString = _configuration.GetConnectionString("CMES_DB")!;
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
SELECT
    Derived.ProductNo AS ModelNo,
    SUM(CASE WHEN Derived.Loc = 'FES'           THEN 1 ELSE 0 END) AS Fes,
    SUM(CASE WHEN Derived.Loc = 'WIP'            THEN 1 ELSE 0 END) AS Wip,
    SUM(CASE WHEN Derived.Loc = 'QUALITY DOCK'   THEN 1 ELSE 0 END) AS QualityDock,
    SUM(CASE WHEN Derived.Loc = 'PAINT LINE'     THEN 1 ELSE 0 END) AS PaintLine,
    SUM(CASE WHEN Derived.Loc = 'TEST CELL LINE' THEN 1 ELSE 0 END) AS TestCellLine,
    SUM(CASE WHEN Derived.Loc = 'PAINT REPAIR'   THEN 1 ELSE 0 END) AS PaintRepair,
    SUM(CASE WHEN Derived.Loc = 'TEST REWORK'    THEN 1 ELSE 0 END) AS TestRework,
    SUM(CASE WHEN Derived.Loc = 'SHORT BUILD'    THEN 1 ELSE 0 END) AS ShortBuild,
    SUM(CASE WHEN Derived.Loc = 'EQA AUDIT'      THEN 1 ELSE 0 END) AS EqaAudit,
    SUM(CASE WHEN Derived.Loc = 'MRA'            THEN 1 ELSE 0 END) AS Mra,
    SUM(CASE WHEN Derived.Loc = 'PE'             THEN 1 ELSE 0 END) AS Pe,
    SUM(CASE WHEN Derived.Loc = 'UNKNOWN'        THEN 1 ELSE 0 END) AS Unknown
FROM (
    SELECT  C.SERIALNO  AS SerialNo,
            P.PRODUCTNO AS ProductNo,
            {LocationSql} AS Loc
    FROM    {BaseSql}
      AND   (@modelNo IS NULL OR P.PRODUCTNO = @modelNo)
) AS Derived
GROUP BY Derived.ProductNo
ORDER BY Derived.ProductNo;";
            cmd.Parameters.AddWithValue("@modelNo", (object?)modelNo ?? DBNull.Value);

            _logger.LogDebug("[ModelTrackingController] FetchSummary SQL (modelNo={ModelNo}):\n{Sql}", modelNo ?? "ALL", cmd.CommandText);

            var rows = new List<ModelSummaryRow>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new ModelSummaryRow
                {
                    ModelNo      = ReadString(reader, "ModelNo") ?? "",
                    Fes          = Convert.ToInt64(reader["Fes"]),
                    Wip          = Convert.ToInt64(reader["Wip"]),
                    QualityDock  = Convert.ToInt64(reader["QualityDock"]),
                    PaintLine    = Convert.ToInt64(reader["PaintLine"]),
                    TestCellLine = Convert.ToInt64(reader["TestCellLine"]),
                    PaintRepair  = Convert.ToInt64(reader["PaintRepair"]),
                    TestRework   = Convert.ToInt64(reader["TestRework"]),
                    ShortBuild   = Convert.ToInt64(reader["ShortBuild"]),
                    EqaAudit     = Convert.ToInt64(reader["EqaAudit"]),
                    Mra          = Convert.ToInt64(reader["Mra"]),
                    Pe           = Convert.ToInt64(reader["Pe"]),
                    Unknown      = Convert.ToInt64(reader["Unknown"]),
                });
            }
            return rows;
        }

        private async Task<List<ModelDetailItem>> FetchDetailsAsync(string modelNo, CancellationToken ct)
        {
            var connectionString = _configuration.GetConnectionString("CMES_DB")!;
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
SELECT
    C.SERIALNO    AS SerialNo,
    P.PRODUCTNO   AS ModelNo,
    C.WORKORDERNO AS WorkOrderNo,
    C.WORKSTATION AS Workstation,
    CASE C.STATUS
        WHEN 1 THEN 'IN-PROD'
        WHEN 2 THEN 'ISSUE'
        WHEN 3 THEN 'FES'
        WHEN 6 THEN 'IN REPAIR'
        ELSE        'UNKNOWN'
    END           AS Status,
    {LocationSql} AS Location,
    DATEADD(MINUTE, 330, C.CREATEDON)    AS BlockLoadTime,
    DATEADD(MINUTE, 330, C.LASTUPDATEON) AS LastUpdatedOn
FROM   {BaseSql}
  AND  P.PRODUCTNO = @modelNo
ORDER  BY Location ASC;";
            cmd.Parameters.AddWithValue("@modelNo", modelNo);

            _logger.LogDebug("[ModelTrackingController] FetchDetails SQL (modelNo={ModelNo}):\n{Sql}", modelNo, cmd.CommandText);

            var rows = new List<ModelDetailItem>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new ModelDetailItem
                {
                    SerialNo      = ReadString(reader,   "SerialNo"),
                    ModelNo       = ReadString(reader,   "ModelNo"),
                    WorkOrderNo   = ReadString(reader,   "WorkOrderNo"),
                    Workstation   = ReadString(reader,   "Workstation"),
                    Status        = ReadString(reader,   "Status"),
                    Location      = ReadString(reader,   "Location"),
                    BlockLoadTime = ReadDateTime(reader, "BlockLoadTime"),
                    LastUpdatedOn = ReadDateTime(reader, "LastUpdatedOn"),
                });
            }
            return rows;
        }

        private static string? ReadString(IDataRecord r, string col)
        {
            var i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i));
        }

        private static DateTime? ReadDateTime(IDataRecord r, string col)
        {
            var i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? null : r.GetDateTime(i);
        }

        // ── Response DTOs ─────────────────────────────────────────────────────

        private sealed class ModelSummaryRow
        {
            public string ModelNo      { get; set; } = "";
            public long   Fes          { get; set; }
            public long   Wip          { get; set; }
            public long   QualityDock  { get; set; }
            public long   PaintLine    { get; set; }
            public long   TestCellLine { get; set; }
            public long   PaintRepair  { get; set; }
            public long   TestRework   { get; set; }
            public long   ShortBuild   { get; set; }
            public long   EqaAudit     { get; set; }
            public long   Mra          { get; set; }
            public long   Pe           { get; set; }
            public long   Unknown      { get; set; }
        }

        private sealed class ModelDetailItem
        {
            public string?   SerialNo      { get; set; }
            public string?   ModelNo       { get; set; }
            public string?   WorkOrderNo   { get; set; }
            public string?   Workstation   { get; set; }
            public string?   Status        { get; set; }
            public string?   Location      { get; set; }
            public DateTime? BlockLoadTime { get; set; }
            public DateTime? LastUpdatedOn { get; set; }
        }
    }
}
