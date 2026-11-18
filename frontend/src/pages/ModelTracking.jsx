/**
 * ModelTracking.jsx
 *
 * Model Wise Tracking — engines grouped by model (PRODUCTNO) x location bucket.
 * Top: pivot summary table (all models by default; one row when searched).
 * Bottom (only once a model is searched): full detail rows for that model.
 */

import { useState, useEffect, useCallback } from 'react'
import * as XLSX from 'xlsx'
import Sidebar from '../components/Sidebar'
import api from '../api/client'
import formatDateTime from '../utils/formatDateTime'
import './WipReport.css'       // shared dash-root/kpi-card/wip-table chrome
import './ModelTracking.css'

const STATUS_COLORS = {
  'IN-PROD':   '#4CAF50',
  'ISSUE':     '#FF9800',
  'IN REPAIR': '#2196F3',
  'FES':       '#9C27B0',
  'UNKNOWN':   '#9E9E9E',
}

// Pivot columns, in the order sir's Oracle query returns them.
const SUMMARY_COLUMNS = [
  { key: 'fes',          label: 'FES' },
  { key: 'wip',          label: 'WIP' },
  { key: 'qualityDock',  label: 'QUALITY DOCK' },
  { key: 'paintLine',    label: 'PAINT LINE' },
  { key: 'testCellLine', label: 'TEST CELL LINE' },
  { key: 'paintRepair',  label: 'PAINT REPAIR' },
  { key: 'testRework',   label: 'TEST REWORK' },
  { key: 'shortBuild',   label: 'SHORT BUILD' },
  { key: 'eqaAudit',     label: 'EQA AUDIT' },
  { key: 'mra',          label: 'MRA' },
  { key: 'pe',           label: 'PE' },
  { key: 'unknown',      label: 'UNKNOWN' },
]

/**
 * GET /api/modeltracking/summary[?modelNo=]
 * No modelNo -> pivot for every model (backend caches this 5 min).
 */
async function fetchSummary(modelNo) {
  const res = await api.get('/modeltracking/summary', { params: modelNo ? { modelNo } : {} })
  return res.data
}

/**
 * GET /api/modeltracking/details?modelNo=
 */
async function fetchDetails(modelNo) {
  const res = await api.get('/modeltracking/details', { params: { modelNo } })
  return res.data.map(row => ({
    serialNo:      row.serialNo,
    modelNo:       row.modelNo,
    blockLoadTime: formatDateTime(row.blockLoadTime),
    jobOrderNo:    row.workOrderNo,
    workstation:   row.workstation,
    status:        row.status,
    location:      row.location,
    lastUpdatedOn: formatDateTime(row.lastUpdatedOn),
  }))
}

function buildFileName(modelNo) {
  const now   = new Date()
  const pad   = n => String(n).padStart(2, '0')
  const stamp = `${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}_${pad(now.getHours())}${pad(now.getMinutes())}`
  return `MODEL_${modelNo}_${stamp}.xlsx`
}

function exportToExcel(rows, modelNo) {
  const sheetData = rows.map(r => ({
    'ESN No':          r.serialNo,
    'Model No':        r.modelNo,
    'Block Load Time': r.blockLoadTime,
    'Job Order No':    r.jobOrderNo,
    'Workstation':     r.workstation,
    'Status':          r.status,
    'Location':        r.location,
    'Last Updated On': r.lastUpdatedOn,
  }))

  const worksheet = XLSX.utils.json_to_sheet(sheetData)
  const workbook  = XLSX.utils.book_new()
  XLSX.utils.book_append_sheet(workbook, worksheet, 'Model Details')

  const colWidths = Object.keys(sheetData[0] ?? {}).map(key => ({
    wch: Math.max(key.length, ...sheetData.map(r => String(r[key] ?? '').length)) + 2,
  }))
  worksheet['!cols'] = colWidths

  XLSX.writeFile(workbook, buildFileName(modelNo))
}

export default function ModelTracking({ user, onLogout, page, onNavigate }) {
  const [modelInput, setModelInput]       = useState('')
  const [searchedModel, setSearchedModel] = useState(null)

  const [summary, setSummary]               = useState([])
  const [summaryLoading, setSummaryLoading] = useState(true)
  const [summaryError, setSummaryError]     = useState(null)

  const [detailRows, setDetailRows]       = useState([])
  const [detailLoading, setDetailLoading] = useState(false)
  const [detailError, setDetailError]     = useState(null)

  const today = new Date().toLocaleDateString('en-IN', {
    day: '2-digit', month: 'short', year: 'numeric',
  })

  const loadSummary = useCallback((modelNo) => {
    setSummaryLoading(true)
    setSummaryError(null)
    fetchSummary(modelNo)
      .then(setSummary)
      .catch(err => setSummaryError(err.message))
      .finally(() => setSummaryLoading(false))
  }, [])

  const loadDetails = useCallback((modelNo) => {
    if (!modelNo) { setDetailRows([]); return }
    setDetailLoading(true)
    setDetailError(null)
    fetchDetails(modelNo)
      .then(setDetailRows)
      .catch(err => setDetailError(err.message))
      .finally(() => setDetailLoading(false))
  }, [])

  // Load the full (all-models) pivot on mount.
  useEffect(() => { loadSummary(null) }, [loadSummary])

  const handleSearch = () => {
    const modelNo = modelInput.trim() || null
    setSearchedModel(modelNo)
    loadSummary(modelNo)
    loadDetails(modelNo)
  }

  const handleClear = () => {
    setModelInput('')
    setSearchedModel(null)
    setDetailRows([])
    loadSummary(null)
  }

  const handleRefresh = () => {
    loadSummary(searchedModel)
    if (searchedModel) loadDetails(searchedModel)
  }

  const totals = SUMMARY_COLUMNS.reduce((acc, col) => {
    acc[col.key] = summary.reduce((sum, row) => sum + Number(row[col.key] ?? 0), 0)
    return acc
  }, {})

  return (
    <div className="dash-root">
      <Sidebar user={user} onLogout={onLogout} page={page} onNavigate={onNavigate} />

      <div className="dash-main">

        {/* ── Top Bar ── */}
        <div className="dash-topbar">
          <div className="dash-topbar-left">
            <span className="dash-page-title">Model Wise Tracking</span>
            <span className="dash-date">{today}</span>
          </div>
          <div className="dash-topbar-right">
            <span className="dash-live-dot" />
            <span className="dash-live-text">Live</span>
            <button
              className="wip-refresh-btn"
              onClick={handleRefresh}
              disabled={summaryLoading || detailLoading}
              title="Refresh model tracking data from the database"
            >
              🔄 Refresh
            </button>
          </div>
        </div>

        <div className="dash-content">

          {/* ── Search bar ── */}
          <div className="model-search-row">
            <label htmlFor="model-no-input">MODEL NO.:</label>
            <input
              id="model-no-input"
              type="text"
              value={modelInput}
              onChange={e => setModelInput(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter') handleSearch() }}
              placeholder="e.g. SO10205"
            />
            <button className="model-search-btn" onClick={handleSearch}>Search</button>
            {searchedModel && (
              <button className="wip-clear-btn" onClick={handleClear}>Clear ✕</button>
            )}
          </div>

          {/* ── Pivot summary table ── */}
          <div className="chart-box model-summary-box">
            <div className="wip-panel-title">
              MODEL WISE TRACKING
              {searchedModel && <span className="wip-panel-sub"> — {searchedModel}</span>}
            </div>
            <div className="wip-table-wrap">
              {summaryError && <div className="wip-empty wip-error">{summaryError}</div>}
              {!summaryError && (
                <table className="wip-table model-summary-table">
                  <thead>
                    <tr>
                      <th>MODEL NO</th>
                      {SUMMARY_COLUMNS.map(col => (
                        <th key={col.key} className="wip-th-num">{col.label}</th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {summaryLoading
                      ? Array.from({ length: 6 }).map((_, i) => (
                          <tr key={i} className="wip-skeleton-row">
                            <td><span className="wip-skeleton" /></td>
                            {SUMMARY_COLUMNS.map(col => (
                              <td key={col.key}><span className="wip-skeleton wip-skeleton-sm" /></td>
                            ))}
                          </tr>
                        ))
                      : summary.length === 0
                        ? (
                          <tr>
                            <td colSpan={SUMMARY_COLUMNS.length + 1} className="wip-empty">
                              No models found.
                            </td>
                          </tr>
                        )
                        : summary.map(row => (
                          <tr key={row.modelNo}>
                            <td>{row.modelNo}</td>
                            {SUMMARY_COLUMNS.map(col => (
                              <td key={col.key} className="wip-th-num">{row[col.key] ?? 0}</td>
                            ))}
                          </tr>
                        ))
                    }
                    {!summaryLoading && summary.length > 0 && (
                      <tr className="wip-total-row">
                        <td>TOTAL</td>
                        {SUMMARY_COLUMNS.map(col => (
                          <td key={col.key} className="wip-th-num">{totals[col.key]}</td>
                        ))}
                      </tr>
                    )}
                  </tbody>
                </table>
              )}
            </div>
          </div>

          {/* ── Detail table — only once a model is searched ── */}
          {searchedModel && (
            <div className="chart-box model-detail-box">
              <div className="wip-panel-title">
                MODEL DETAILS
                <span className="wip-panel-sub"> — {searchedModel}</span>
                <button
                  className="wip-export-btn"
                  onClick={() => exportToExcel(detailRows, searchedModel)}
                  disabled={detailLoading || detailRows.length === 0}
                  title={detailRows.length === 0 ? 'No rows to export' : `Export ${detailRows.length} rows to Excel`}
                >
                  ⬇ Export Excel
                </button>
              </div>

              <div className="wip-table-wrap">
                {detailError && <div className="wip-empty wip-error">{detailError}</div>}
                {!detailError && detailLoading && (
                  <div className="wip-empty">Loading…</div>
                )}
                {!detailError && !detailLoading && detailRows.length === 0 && (
                  <div className="wip-empty">No records for this model.</div>
                )}
                {!detailError && !detailLoading && detailRows.length > 0 && (
                  <table className="wip-table wip-detail-table">
                    <thead>
                      <tr>
                        <th>ESN NO</th>
                        <th>MODEL NO</th>
                        <th>BLOCK LOAD TIME</th>
                        <th>JOB ORDER NO</th>
                        <th>WORKSTATION</th>
                        <th>STATUS</th>
                        <th>LOCATION</th>
                        <th>LAST UPDATED ON</th>
                      </tr>
                    </thead>
                    <tbody>
                      {detailRows.map((row, idx) => (
                        <tr key={idx}>
                          <td className="wip-mono">{row.serialNo}</td>
                          <td>{row.modelNo}</td>
                          <td className="wip-mono">{row.blockLoadTime}</td>
                          <td className="wip-mono">{row.jobOrderNo}</td>
                          <td>{row.workstation}</td>
                          <td>
                            <span
                              className="wip-status-badge"
                              style={{
                                background:  `${STATUS_COLORS[row.status] ?? '#555'}22`,
                                color:        STATUS_COLORS[row.status] ?? '#aaa',
                                borderColor: `${STATUS_COLORS[row.status] ?? '#555'}55`,
                              }}
                            >
                              {row.status}
                            </span>
                          </td>
                          <td>{row.location}</td>
                          <td className="wip-mono">{row.lastUpdatedOn}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
              </div>
            </div>
          )}

        </div>{/* /dash-content */}
      </div>{/* /dash-main */}
    </div>
  )
}
