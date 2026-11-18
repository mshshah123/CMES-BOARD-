/**
 * formatDateTime(value) -> "dd-mm-yyyy HH:mm:ss"
 *
 * Explicit build — toLocaleString('en-IN') renders dd/mm/yyyy with slashes,
 * not the dashed format the WIP Report / Model Tracking screens need.
 * Shared by any page displaying Block Load Time / Last Updated On columns.
 */
export default function formatDateTime(value) {
  if (!value) return ''
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value

  const pad  = n => String(n).padStart(2, '0')
  const dd   = pad(date.getDate())
  const mm   = pad(date.getMonth() + 1)
  const yyyy = date.getFullYear()
  const hh   = pad(date.getHours())
  const mi   = pad(date.getMinutes())
  const ss   = pad(date.getSeconds())
  return `${dd}-${mm}-${yyyy} ${hh}:${mi}:${ss}`
}
