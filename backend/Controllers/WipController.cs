using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.SqlClient;
using System.Data;

namespace CMES.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Policy = "CmesUser")]   // dashboard jaisa - sirf active CMES user
    public class WipController : ControllerBase
    {
        // ── Dependencies ──────────────────────────────────────────────────────
        // Raw SqlConnection (CMES_DB) use hota; CmesDbContext ki zaroorat nahi (pooled factory se direct inject bhi nahi hota).
        private readonly IConfiguration         _configuration;
        private readonly ILogger<WipController> _logger;

        // ── WIP base filter ───────────────────────────────────────────────────
        // Source table is MPI_COB_T_SERIAL_NO_HISTORY — the SQL Server equivalent
        // of the Oracle COB_T_SERIAL_NO table.  MPI_COB_T_SERIAL_NO only holds
        // SERIALNO / WORKORDERNO / STATUS / CREATEDON and does NOT have WORKSTATION,
        // LOCATION, PRODUCTID, or LASTUPDATEON.
        private const string WipBaseSql = @"
    dbo.MPI_COB_T_SERIAL_NO C
    INNER JOIN dbo.MPI_PRODUCT P ON P.ID = C.PRODUCTID
    WHERE  C.STATUS   IN (1, 2, 6)
    AND LEN(C.SERIALNO) = 8
    AND C.CREATEDON    >= '2025-08-01'";

        // ── Containment flag ──────────────────────────────────────────────────
        // Oracle source-of-truth (TCL1PROD data set): a serial is CONTAINMENT
        // when there is an outbound transaction whose TRANSACTIONID starts with
        // 'ENGCONT' and OVERALLSTATUS = 1. This overrides the numeric STATUS
        // label (IN-PROD/ISSUE/IN REPAIR) wherever it applies.
        private const string ContainmentExistsSql = @"EXISTS (
    SELECT 1 FROM dbo.MPI_COB_T_TRANSACTION_OUTBOUND T
    WHERE T.SERIALNO = C.SERIALNO
      AND T.TRANSACTIONID LIKE 'ENGCONT%'
      AND T.OVERALLSTATUS = 1
)";

        // ── Location derivation — faithful port of the Oracle CASE expression ──
        //
        // Branch order (must not be reordered):
        //   1. Specific LOCATION text overrides (ATP REPAIR, BLB REPAIR, PART SHORTAGE)
        //   2. Pass-through: any other non-null LOCATION is used as-is
        //      (covers EQA AUDIT, PE, TEST REWORK, PAINT REPAIR, QUALITY DOCK, MRA, etc.)
        //   3. WORKSTATION numeric ranges (only reached when LOCATION IS NULL)
        //   4. ELSE UNKNOWN
        //
        // Oracle returns 'OLDLINE' and 'NEWLINE' (no spaces).
        // The frontend currently displays "OLD LINE" / "NEW LINE" (with spaces).
        // A mapping layer in NormalizeOracleLocation() reconciles them so the
        // frontend labels are preserved without touching the CASE expression.
        private const string DeriveLocationSql = @"
CASE
    -- Branch 1: specific LOCATION overrides
    WHEN C.LOCATION = 'ATP REPAIR'    THEN 'NEWLINE LOOP'
    WHEN C.LOCATION = 'BLB REPAIR'    THEN 'TEST REWORK'
    WHEN C.LOCATION = 'PART SHORTAGE' THEN 'SHORT BUILD'
    WHEN C.LOCATION = 'RE-TEST' THEN 'TEST CELL LINE'
    WHEN C.LOCATION = 'PAINT MRA' THEN 'PAINT REPAIR'
    WHEN C.LOCATION = 'PAINT REPAIR' THEN 'PAINT REPAIR'
    WHEN C.LOCATION = 'EQA' THEN 'EQA AUDIT'

    -- Branch 2: pass-through for any other non-null LOCATION (Oracle: TO_CHAR(C.LOCATION))
    WHEN C.LOCATION IS NOT NULL       THEN C.LOCATION

    -- Branch 3: WORKSTATION-range rules (only reached when LOCATION IS NULL)
    WHEN C.WORKSTATION = '10008'
        THEN 'LINESET'
    WHEN C.WORKSTATION BETWEEN '10000' AND '13000'
        THEN 'LINESET LINE'
    WHEN C.WORKSTATION BETWEEN '20000' AND '23900'
        THEN 'OLDLINE'
    WHEN C.WORKSTATION BETWEEN '30000' AND '33200'
      OR C.WORKSTATION = 'TC1CMW101MINIE1'
        THEN 'NEWLINE'
    WHEN C.WORKSTATION BETWEEN '40000' AND '44600'
        THEN 'TEST CELL LINE'
    WHEN C.WORKSTATION BETWEEN '50000' AND '52105'
        THEN 'PAINT LINE'
    WHEN C.WORKSTATION = '54000'
        THEN 'PAINT REPAIR'
    WHEN C.WORKSTATION IN ('52000','52100','52200','55000')
        THEN 'QUALITY DOCK'
    WHEN C.WORKSTATION IN ('33300','33400')
        THEN 'NEWLINE LOOP'
    WHEN C.WORKSTATION = '34000'
        THEN 'MRA'
    WHEN C.WORKSTATION IN ('35000','35100')
        THEN 'EQA AUDIT'

    ELSE 'UNKNOWN'
END";

        // ── Ordered category list (matches legacy display order) ──────────────
        private static readonly (int Order, string Name)[] Categories =
        [
            ( 1, "LINESET"       ),
            ( 2, "LINESET LINE"  ),
            ( 3, "OLD LINE"      ),
            ( 4, "NEW LINE"      ),
            ( 5, "TEST CELL LINE"),
            ( 6, "PAINT LINE"    ),
            ( 7, "QUALITY DOCK"  ),
            ( 8, "PAINT HOLD"  ),
            ( 9, "PAINT REPAIR"  ),
            (10, "NEWLINE LOOP"  ),
            (11, "MRA"           ),
            (12, "EQA AUDIT"     ),
            (13, "TEST REWORK"   ),
            (14, "PE"            ),
            (15, "SHORT BUILD"   ),
            (16, "UNKNOWN"       )
        ];

        // ── Route slug → canonical name (unchanged from previous version) ─────
        private static readonly IReadOnlyDictionary<string, string> LocationRouteAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lineset"]         = "LINESET",
                ["linesetline"]     = "LINESET LINE",
                ["lineset-line"]    = "LINESET LINE",
                ["oldline"]         = "OLD LINE",
                ["old-line"]        = "OLD LINE",
                ["newline"]         = "NEW LINE",
                ["new-line"]        = "NEW LINE",
                ["testcell"]        = "TEST CELL LINE",
                ["test-cell"]       = "TEST CELL LINE",
                ["testcellline"]    = "TEST CELL LINE",
                ["test-cell-line"]  = "TEST CELL LINE",
                ["paintline"]       = "PAINT LINE",
                ["paint-line"]      = "PAINT LINE",
                ["painthold"]       = "PAINT HOLD",
                ["paint-hold"]      = "PAINT HOLD",
                ["paintrepair"]     = "PAINT REPAIR",
                ["paint-repair"]    = "PAINT REPAIR",
                ["qualitydock"]     = "QUALITY DOCK",
                ["quality-dock"]    = "QUALITY DOCK",
                ["newlineloop"]     = "NEWLINE LOOP",
                ["newline-loop"]    = "NEWLINE LOOP",
                ["mra"]             = "MRA",
                ["eqaaudit"]        = "EQA AUDIT",
                ["eqa-audit"]       = "EQA AUDIT",
                ["testrework"]      = "TEST REWORK",
                ["test-rework"]     = "TEST REWORK",
                ["pe"]              = "PE",
                ["shortbuild"]      = "SHORT BUILD",
                ["short-build"]     = "SHORT BUILD",
                ["unknown"]         = "UNKNOWN",
            };

        public WipController(ILogger<WipController> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        // ══════════════════════════════════════════════════════════════════════
        // GET /api/wip/test
        // ══════════════════════════════════════════════════════════════════════
        [HttpGet("test")]
        public IActionResult Test() =>
            Ok(new { message = "WIP API is working!", timestamp = DateTime.UtcNow });

        // ══════════════════════════════════════════════════════════════════════
        // GET /api/wip/summary
        // Returns the 5 WIP Report KPI cards: Total WIP, In Prod, Issue, Repair,
        // Containment. In Prod/Issue/Repair each exclude contained serials
        // (Containment overrides the status label, same as the detail grid).
        // ══════════════════════════════════════════════════════════════════════
        [HttpGet("summary")]
        public async Task<IActionResult> GetSummary(CancellationToken ct)
        {
            try
            {
                _logger.LogInformation("[WipController] GetSummary: fetching status counts.");
                var counts = await FetchStatusCountsAsync(ct);

                _logger.LogInformation(
                    "[WipController] GetSummary: totalWip={Total} inProd={InProd} issue={Issue} repair={Repair} containment={Containment}.",
                    counts.TotalWip, counts.InProd, counts.Issue, counts.Repair, counts.Containment);

                return Ok(new
                {
                    totalWip    = counts.TotalWip,
                    inProd      = counts.InProd,
                    issue       = counts.Issue,
                    repair      = counts.Repair,
                    containment = counts.Containment,
                });
            }
            catch (SqlException ex)
            {
                _logger.LogError(ex, "[WipController] GetSummary SQL error.");
                return StatusCode(500, new { message = "Unable to load WIP summary.", detail = ex.Message });
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // GET /api/wip/locations
        // Returns per-category counts using COUNT(DISTINCT SERIALNO).
        // Category counts are fetched in parallel (Task.WhenAll).
        // ══════════════════════════════════════════════════════════════════════
        [HttpGet("locations")]
        public async Task<IActionResult> GetLocations(CancellationToken ct)
        {
            try
            {
                // Probe for PRODUCT table once so we know whether to attempt joins later
                var productExists = await ProductTableExistsAsync(ct);
                _logger.LogInformation("[WipController] GetLocations: dbo.PRODUCT exists = {Exists}.", productExists);

                _logger.LogInformation("[WipController] GetLocations: fetching category counts in parallel.");
                var counts = await FetchCategoryCountsAsync(ct);

                // NormalizeOracleLocation maps Oracle values ('OLDLINE','NEWLINE') to
                // frontend labels ('OLD LINE','NEW LINE') in the category name key.
                var result = Categories
                    .Select(c => new
                    {
                        location = NormalizeOracleLocation(c.Name),
                        count    = counts.TryGetValue(c.Name, out var n) ? n : 0L,
                    })
                    .ToList();

                _logger.LogInformation("[WipController] GetLocations: returning {Count} categories.", result.Count);
                return Ok(result);
            }
            catch (SqlException ex)
            {
                _logger.LogError(ex, "[WipController] GetLocations SQL error.");
                return StatusCode(500, new { message = "Unable to load WIP location counts.", detail = ex.Message });
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // GET /api/wip/details[/{location}]?page=1&pageSize=100
        //
        // All filtering, sorting, and pagination is done in SQL.
        // Nothing is loaded into memory before it reaches the SELECT list.
        // ══════════════════════════════════════════════════════════════════════
        [HttpGet("details")]
        [HttpGet("details/{location}")]
        public async Task<IActionResult> GetDetails(
            string?           location = null,
            [FromQuery] int   page     = 1,
            [FromQuery] int   pageSize = 500,
            CancellationToken ct       = default)
        {
            pageSize = Math.Clamp(pageSize, 1, 500);
            page     = Math.Max(page, 1);

            var canonicalLocation = NormalizeLocationRoute(location);
            _logger.LogInformation("[WipController] GetDetails: location='{Location}' canonical='{Canonical}' page={Page} pageSize={PageSize}.",
                location ?? "(all)", canonicalLocation ?? "(all)", page, pageSize);

            try
            {
                var (rows, totalCount) = await FetchDetailPageAsync(
                    canonicalLocation, page, pageSize, ct);

                return Ok(new
                {
                    page,
                    pageSize,
                    totalCount,
                    totalPages = (int)Math.Ceiling((double)totalCount / pageSize),
                    items      = rows,
                });
            }
            catch (SqlException ex)
            {
                _logger.LogError(ex,
                    "[WipController] GetDetails SQL error for location '{Location}'.", canonicalLocation ?? "ALL");
                return StatusCode(500, new { message = "Unable to load WIP details.", detail = ex.Message });
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Private helpers
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns DISTINCT-serial counts for every category in a SINGLE query.
        /// One pass over the table: derive each row's location with the SAME CASE
        /// the details endpoint uses, filter once via WipBaseSql, then
        /// COUNT(DISTINCT SERIALNO) per group. Replaces the old 16-queries-in-parallel
        /// approach (16 connections + 16 scans) and keeps the location cards exactly
        /// consistent with the details grid.
        /// </summary>
        private async Task<Dictionary<string, long>> FetchCategoryCountsAsync(CancellationToken ct)
        {
            // Seed every known category at 0 so each card always has a value.
            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, name) in Categories)
                result[name] = 0L;

            var connectionString = _configuration.GetConnectionString("CMES_DB")!;
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
SELECT   Derived.DerivedLocation,
         COUNT(DISTINCT Derived.SerialNo) AS Cnt
FROM (
    SELECT  C.SERIALNO          AS SerialNo,
            {DeriveLocationSql}  AS DerivedLocation
    FROM    {WipBaseSql}
) AS Derived
GROUP BY Derived.DerivedLocation;";

            _logger.LogDebug("[WipController] FetchCategoryCounts single-query SQL:\n{Sql}", cmd.CommandText);
            try
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    // 'OLDLINE'/'NEWLINE' -> 'OLD LINE'/'NEW LINE'; everything else as-is.
                    var label = NormalizeOracleLocation(ReadString(reader, "DerivedLocation"));
                    var cnt   = reader["Cnt"] is DBNull or null ? 0L : Convert.ToInt64(reader["Cnt"]);

                    // Only assign to known categories; stray pass-through locations are ignored.
                    if (result.ContainsKey(label))
                        result[label] = cnt;
                }
            }
            catch (SqlException ex)
            {
                _logger.LogError(ex, "[WipController] FetchCategoryCounts SQL error.");
                throw;
            }

            return result;
        }


        /// <summary>
        /// Fetches one page of detail rows for a given canonical location.
        /// All filtering, sorting, and pagination happens in SQL (OFFSET/FETCH).
        /// </summary>
        private async Task<(List<WipDetailItem> Rows, long TotalCount)> FetchDetailPageAsync(
            string? canonicalLocation, int page, int pageSize, CancellationToken ct)
        {
            var connectionString = _configuration.GetConnectionString("CMES_DB")!;
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);

            // The CASE expression produces Oracle-style values ('OLDLINE', 'NEWLINE').
            // Translate the frontend canonical label to the Oracle value before filtering.
            var oracleLocation = ToOracleLocation(canonicalLocation);

            long totalCount;
            await using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = $@"
SELECT COUNT(DISTINCT C.SERIALNO)
FROM   {WipBaseSql}
  AND  (@location IS NULL OR ({DeriveLocationSql}) = @location);";
                countCmd.Parameters.AddWithValue("@location",
                    (object?)oracleLocation ?? DBNull.Value);

                _logger.LogDebug("[WipController] FetchDetailPage COUNT SQL (location={Location}):\n{Sql}",
                    oracleLocation ?? "ALL", countCmd.CommandText);
                try
                {
                    var scalar = await countCmd.ExecuteScalarAsync(ct);
                    totalCount = scalar is DBNull or null ? 0L : Convert.ToInt64(scalar);
                    _logger.LogInformation("[WipController] FetchDetailPage total count = {Count} for location '{Location}'.",
                        totalCount, oracleLocation ?? "ALL");
                }
                catch (SqlException ex)
                {
                    _logger.LogError(ex, "[WipController] FetchDetailPage COUNT SQL error for location '{Location}'.", oracleLocation ?? "ALL");
                    throw;
                }
            }

            var rows = new List<WipDetailItem>((int)Math.Min(pageSize, totalCount));

            if (totalCount > 0)
            {
                int offset = (page - 1) * pageSize;

                await using var pageCmd = conn.CreateCommand();
                pageCmd.CommandText = $@"
SELECT
    C.SERIALNO                  AS SerialNo,
    P.PRODUCTNO                 AS ProductId,
    C.WORKORDERNO               AS WorkOrderNo,
    C.WORKSTATION               AS Workstation,
    CASE
        WHEN {ContainmentExistsSql} THEN 'CONTAINMENT'
        WHEN C.STATUS = 1           THEN 'IN-PROD'
        WHEN C.STATUS = 2           THEN 'ISSUE'
        WHEN C.STATUS = 6           THEN 'IN REPAIR'
        ELSE                             'UNKNOWN'
    END                         AS Status,
    {DeriveLocationSql}         AS Location,
    DATEADD(MINUTE, 330, C.CREATEDON) AS CreatedOn,
    DATEADD(MINUTE, 330, C.LASTUPDATEON)   AS LastUpdatedOn
FROM   {WipBaseSql}
  AND  (@location IS NULL OR ({DeriveLocationSql}) = @location)
ORDER  BY C.CREATEDON DESC
OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;";

                pageCmd.Parameters.AddWithValue("@location",
                    (object?)oracleLocation ?? DBNull.Value);
                pageCmd.Parameters.AddWithValue("@offset",   offset);
                pageCmd.Parameters.AddWithValue("@pageSize", pageSize);

                _logger.LogDebug("[WipController] FetchDetailPage PAGE SQL page={Page} pageSize={PageSize} location={Location}.",
                    page, pageSize, oracleLocation ?? "ALL");
                try
                {
                    await using var reader = await pageCmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        // NormalizeOracleLocation maps 'OLDLINE'→'OLD LINE', 'NEWLINE'→'NEW LINE'
                        // so the frontend receives the labels it already displays.
                        rows.Add(new WipDetailItem
                        {
                            SerialNo      = ReadString(reader,   "SerialNo"),
                            ProductId     = ReadString(reader,   "ProductId"),
                            WorkOrderNo   = ReadString(reader,   "WorkOrderNo"),
                            Workstation   = ReadString(reader,   "Workstation"),
                            Status        = ReadString(reader,   "Status"),
                            Location      = NormalizeOracleLocation(ReadString(reader, "Location")),
                            CreatedOn     = ReadDateTime(reader, "CreatedOn"),
                            LastUpdatedOn = ReadDateTime(reader, "LastUpdatedOn"),
                        });
                    }
                    _logger.LogInformation("[WipController] FetchDetailPage returned {RowCount} rows for location '{Location}' page {Page}.",
                        rows.Count, oracleLocation ?? "ALL", page);
                }
                catch (SqlException ex)
                {
                    _logger.LogError(ex, "[WipController] FetchDetailPage PAGE SQL error for location '{Location}'.", oracleLocation ?? "ALL");
                    throw;
                }
            }

            return (rows, totalCount);
        }

        /// <summary>
        /// Fetches the 5 WIP Report KPI counts in a single grouped query.
        /// Containment takes precedence over the numeric status, so a contained
        /// serial is counted under Containment only (not also under its
        /// underlying In Prod/Issue/Repair status) — matching the detail grid.
        /// </summary>
        private async Task<WipStatusCounts> FetchStatusCountsAsync(CancellationToken ct)
        {
            var connectionString = _configuration.GetConnectionString("CMES_DB")!;
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            // SQL Server rejects COUNT(DISTINCT CASE WHEN EXISTS(...) ...) directly
            // ("Cannot perform an aggregate function on an expression containing an
            // aggregate or a subquery") — so the containment EXISTS check is resolved
            // into a plain bit column in a derived table first, then aggregated.
            cmd.CommandText = $@"
SELECT
    COUNT(DISTINCT Derived.SerialNo) AS TotalWip,
    COUNT(DISTINCT CASE WHEN Derived.IsContained = 0 AND Derived.Status = 1 THEN Derived.SerialNo END) AS InProd,
    COUNT(DISTINCT CASE WHEN Derived.IsContained = 0 AND Derived.Status = 2 THEN Derived.SerialNo END) AS Issue,
    COUNT(DISTINCT CASE WHEN Derived.IsContained = 0 AND Derived.Status = 6 THEN Derived.SerialNo END) AS Repair,
    COUNT(DISTINCT CASE WHEN Derived.IsContained = 1 THEN Derived.SerialNo END) AS Containment
FROM (
    SELECT  C.SERIALNO AS SerialNo,
            C.STATUS   AS Status,
            CASE WHEN {ContainmentExistsSql} THEN 1 ELSE 0 END AS IsContained
    FROM    {WipBaseSql}
) AS Derived;";

            _logger.LogDebug("[WipController] FetchStatusCounts SQL:\n{Sql}", cmd.CommandText);
            try
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                return new WipStatusCounts
                {
                    TotalWip    = Convert.ToInt64(reader["TotalWip"]),
                    InProd      = Convert.ToInt64(reader["InProd"]),
                    Issue       = Convert.ToInt64(reader["Issue"]),
                    Repair      = Convert.ToInt64(reader["Repair"]),
                    Containment = Convert.ToInt64(reader["Containment"]),
                };
            }
            catch (SqlException ex)
            {
                _logger.LogError(ex, "[WipController] FetchStatusCounts SQL error.");
                throw;
            }
        }

        // ── Low-level helpers (unchanged) ─────────────────────────────────────

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

        private static string? NormalizeLocationRoute(string? location)
        {
            if (string.IsNullOrWhiteSpace(location))
                return null;

            var trimmed = location.Trim();
            if (LocationRouteAliases.TryGetValue(trimmed, out var alias))
                return alias;

            var compact = trimmed.Replace(" ", "", StringComparison.Ordinal);
            if (LocationRouteAliases.TryGetValue(compact, out alias))
                return alias;

            return trimmed.Replace('-', ' ').ToUpperInvariant();
        }

        /// <summary>
        /// Maps Oracle CASE output values to the frontend display labels.
        /// Oracle returns 'OLDLINE' and 'NEWLINE'; the frontend expects 'OLD LINE'
        /// and 'NEW LINE'.  All other values are returned unchanged.
        /// </summary>
        private static string NormalizeOracleLocation(string? raw)
        {
            return raw switch
            {
                "OLDLINE" => "OLD LINE",
                "NEWLINE" => "NEW LINE",
                null      => "UNKNOWN",
                _         => raw,
            };
        }

        /// <summary>
        /// Returns the Oracle location value expected by the CASE expression for
        /// a given frontend label.  Needed when filtering details by location:
        /// the frontend sends "OLD LINE", but DeriveLocationSql produces "OLDLINE".
        /// </summary>
        private static string? ToOracleLocation(string? frontendLabel)
        {
            return frontendLabel switch
            {
                "OLD LINE" => "OLDLINE",
                "NEW LINE" => "NEWLINE",
                null       => null,
                _          => frontendLabel,
            };
        }

        /// <summary>
        /// Probes whether dbo.PRODUCT exists in the current database.
        /// Called once on startup / first use before any PRODUCT JOIN is attempted.
        /// Result is cached so the probe only runs once per application lifetime.
        /// </summary>
        private bool? _productTableExists;

        private async Task<bool> ProductTableExistsAsync(CancellationToken ct)
        {
            if (_productTableExists.HasValue)
                return _productTableExists.Value;

            var cs = _configuration.GetConnectionString("CMES_DB")!;
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT COUNT(1)
FROM   INFORMATION_SCHEMA.TABLES
WHERE  TABLE_SCHEMA = 'dbo'
  AND  TABLE_NAME   = 'MPI_PRODUCT';";
            var scalar = await cmd.ExecuteScalarAsync(ct);
            _productTableExists = Convert.ToInt32(scalar) > 0;
            _logger.LogInformation("[WipController] dbo.MPI_PRODUCT table exists: {Exists}", _productTableExists);
            return _productTableExists.Value;
        }

        // ── Response DTOs ─────────────────────────────────────────────────────

        private sealed class WipStatusCounts
        {
            public long TotalWip    { get; set; }
            public long InProd      { get; set; }
            public long Issue       { get; set; }
            public long Repair      { get; set; }
            public long Containment { get; set; }
        }

        private sealed class WipDetailItem
        {
            public string?   SerialNo      { get; set; }
            public string?   ProductId     { get; set; }
            public string?   WorkOrderNo   { get; set; }
            public string?   Workstation   { get; set; }
            public string?   Status        { get; set; }
            public string?   Location      { get; set; }
            public DateTime? CreatedOn     { get; set; }
            public DateTime? LastUpdatedOn { get; set; }
        }
    }
}
