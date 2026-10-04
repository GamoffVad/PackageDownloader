/**
 * Локальный NuGet-реестр (NuGet V3 API).
 * Использует только встроенные модули Node.js (нет внешних зависимостей).
 *
 * Env vars:
 *   NUGET_SERVER_PORT   — порт (по умолчанию 5000)
 *   NUGET_PACKAGES_DIR  — папка с пакетами: <id>/<version>/<id.version.nupkg>
 *
 * На клиенте в nuget.config:
 *   <add key="Local" value="http://<имя-сервера>:<порт>/v3/index.json" />
 *   После чего — dotnet restore / dotnet build как обычно.
 */

import { createServer }                          from 'node:http'
import { createReadStream, readdirSync, statSync,
         readFileSync }                          from 'node:fs'
import { inflateRawSync }                        from 'node:zlib'
import { join, extname, basename, dirname }      from 'node:path'

const PORT = parseInt(process.env.NUGET_SERVER_PORT  ?? '5000', 10)
const DIR  = process.env.NUGET_PACKAGES_DIR ?? ''

// Как часто перечитывать папку с пакетами (мс) — чтобы новые загрузки
// подхватывались без перезапуска сервера.
const RESCAN_MS = 5000

// ── Разбор зависимостей из XML .nuspec (через RegExp) ────────────────────────
function parseDeps(xml) {
  if (!xml) return []
  const seen = new Set()
  const deps = []
  const re   = /<dependency[^>]+id="([^"]+)"[^>]+version="([^"]+)"/gi
  let m
  while ((m = re.exec(xml)) !== null) {
    const key = m[1].toLowerCase()
    if (!seen.has(key)) { seen.add(key); deps.push({ id: m[1], range: m[2] }) }
  }
  return deps
}

// ── Минимальное чтение ZIP: достаём .nuspec прямо из .nupkg ──────────────────
// .nupkg — обычный zip-архив; читаем центральный каталог и распаковываем
// нужную запись (deflate или stored). Внешние зависимости не нужны.
function readEntryFromZip(zipPath, matchName) {
  try {
    const buf = readFileSync(zipPath)

    // End of Central Directory — ищем сигнатуру с конца.
    let eocd = -1
    for (let i = buf.length - 22; i >= 0 && i > buf.length - 66_000; i--) {
      if (buf.readUInt32LE(i) === 0x06054b50) { eocd = i; break }
    }
    if (eocd < 0) return null

    let count  = buf.readUInt16LE(eocd + 10)
    let offset = buf.readUInt32LE(eocd + 16)

    for (let i = 0; i < count; i++) {
      if (buf.readUInt32LE(offset) !== 0x02014b50) break
      const method   = buf.readUInt16LE(offset + 10)
      const compSize = buf.readUInt32LE(offset + 20)
      const nameLen  = buf.readUInt16LE(offset + 28)
      const extraLen = buf.readUInt16LE(offset + 30)
      const cmtLen   = buf.readUInt16LE(offset + 32)
      const localOff = buf.readUInt32LE(offset + 42)
      const name     = buf.toString('utf8', offset + 46, offset + 46 + nameLen)

      if (matchName(name)) {
        const lNameLen  = buf.readUInt16LE(localOff + 26)
        const lExtraLen = buf.readUInt16LE(localOff + 28)
        const start     = localOff + 30 + lNameLen + lExtraLen
        const raw       = buf.subarray(start, start + compSize)
        return method === 0 ? raw.toString('utf8') : inflateRawSync(raw).toString('utf8')
      }

      offset += 46 + nameLen + extraLen + cmtLen
    }
  } catch { /* повреждённый архив — вернём null */ }
  return null
}

// ── Метаданные одного .nupkg ─────────────────────────────────────────────────
// id и версия берутся из .nuspec внутри пакета; если прочитать не вышло —
// из соседнего metadata.nuspec, затем из структуры папок <id>/<версия>/,
// и в последнюю очередь из имени файла «<id>.<версия>.nupkg».
function readPackageMeta(nupkgPath) {
  let xml = readEntryFromZip(nupkgPath, n => !n.includes('/') && n.toLowerCase().endsWith('.nuspec'))

  if (!xml) {
    const dir = dirname(nupkgPath)
    try {
      const sibling = readdirSync(dir).find(f =>
        f === 'metadata.nuspec' || extname(f).toLowerCase() === '.nuspec')
      if (sibling) xml = readFileSync(join(dir, sibling), 'utf8')
    } catch { /* ignore */ }
  }

  let id      = xml?.match(/<id>\s*([^<]+?)\s*<\/id>/i)?.[1]
  let version = xml?.match(/<version>\s*([^<]+?)\s*<\/version>/i)?.[1]

  if (!id || !version) {
    // Раскладка <id>/<версия>/файл.nupkg
    const verDir = basename(dirname(nupkgPath))
    const idDir  = basename(dirname(dirname(nupkgPath)))
    if (/^\d/.test(verDir) && idDir) { id ??= idDir; version ??= verDir }
  }

  if (!id || !version) {
    // «Newtonsoft.Json.13.0.3.nupkg» → id + версия с первой цифровой части.
    const file = basename(nupkgPath, extname(nupkgPath))
    const m    = file.match(/^(.+?)\.(\d[\d.]*(?:-[0-9A-Za-z.-]+)?)$/)
    if (m) { id ??= m[1]; version ??= m[2] }
  }

  if (!id || !version) return null
  return { id, version, deps: parseDeps(xml) }
}

// ── Рекурсивный обход папки: все .nupkg на любой глубине ─────────────────────
function walkNupkg(dir, out = []) {
  let entries
  try { entries = readdirSync(dir, { withFileTypes: true }) } catch { return out }
  for (const e of entries) {
    const p = join(dir, e.name)
    if (e.isDirectory()) walkNupkg(p, out)
    else if (extname(e.name).toLowerCase() === '.nupkg') out.push(p)
  }
  return out
}

// ── Построение реестра ───────────────────────────────────────────────────────
// registry: { lowerId → { id: 'OriginalId', versions: { '1.0.0' → { nupkgPath, deps } } } }
let registry  = {}
let pkgCount  = 0
let lastScan  = 0
const metaCache = new Map()   // путь → { mtimeMs, size, meta }

function buildRegistry(dir) {
  const reg = {}
  if (!dir) return reg

  for (const nupkgPath of walkNupkg(dir)) {
    let stat
    try { stat = statSync(nupkgPath) } catch { continue }

    // Разбираем архив только для новых/изменившихся файлов.
    const cached = metaCache.get(nupkgPath)
    let meta
    if (cached && cached.mtimeMs === stat.mtimeMs && cached.size === stat.size) {
      meta = cached.meta
    } else {
      meta = readPackageMeta(nupkgPath)
      metaCache.set(nupkgPath, { mtimeMs: stat.mtimeMs, size: stat.size, meta })
    }
    if (!meta) continue

    const lId = meta.id.toLowerCase()
    if (!reg[lId]) reg[lId] = { id: meta.id, versions: {} }
    reg[lId].versions[meta.version] = { nupkgPath, deps: meta.deps }
  }

  // Версии — по возрастанию, чтобы latest определялся корректно.
  for (const pkg of Object.values(reg)) {
    pkg.versions = Object.fromEntries(
      Object.entries(pkg.versions).sort((a, b) => compareVersions(a[0], b[0])))
  }
  return reg
}

// Сравнение версий NuGet: числовые части, затем пред-релиз (он старше релиза).
function compareVersions(a, b) {
  const [ra, pa] = a.split('-')
  const [rb, pb] = b.split('-')
  const na = ra.split('.').map(Number)
  const nb = rb.split('.').map(Number)
  for (let i = 0; i < Math.max(na.length, nb.length); i++) {
    const diff = (na[i] ?? 0) - (nb[i] ?? 0)
    if (diff !== 0) return diff
  }
  if (!pa && !pb) return 0
  if (!pa) return 1
  if (!pb) return -1
  return pa.localeCompare(pb)
}

/// Перечитывает папку, если прошло больше RESCAN_MS с прошлого раза.
function rescanIfStale() {
  if (Date.now() - lastScan < RESCAN_MS) return
  lastScan = Date.now()
  const before = pkgCount
  registry = buildRegistry(DIR)
  pkgCount = Object.keys(registry).length
  if (before !== pkgCount) console.log(`[nuget-server] Пакетов в папке: ${pkgCount}`)
}

rescanIfStale()
console.log(`[nuget-server] Папка:             ${DIR || '(не задана)'}`)
console.log(`[nuget-server] Загружено пакетов: ${pkgCount}`)
console.log(`[nuget-server] Запуск на порту:   ${PORT}…`)

// ── HTTP-сервер ───────────────────────────────────────────────────────────────
createServer((req, res) => {
  const host = req.headers.host ?? `localhost:${PORT}`
  const base = `http://${host}`
  const url  = new URL(req.url, base)
  const p    = url.pathname

  // Лог каждого запроса
  const startedAt = Date.now()
  res.on('finish', () => {
    const t  = new Date().toTimeString().slice(0, 8)
    const ms = Date.now() - startedAt
    const ip = (req.socket.remoteAddress ?? '').replace('::ffff:', '') || '-'
    console.log(`[${t}] ${req.method} ${decodeURIComponent(p)} → ${res.statusCode} (${ms}ms, ${ip})`)
  })

  const json = (code, data) => {
    const body = JSON.stringify(data)
    res.writeHead(code, { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) })
    res.end(body)
  }

  // Подхватываем пакеты, скачанные уже после запуска сервера.
  rescanIfStale()

  // ── /v3/index.json  (Service Index) ─────────────────────────────────────
  if (p === '/v3/index.json') {
    json(200, {
      version:   '3.0.0',
      resources: [
        { '@id': `${base}/v3/flatcontainer/`, '@type': 'PackageBaseAddress/3.0.0',       comment: 'Package download base URL.' },
        { '@id': `${base}/v3/registration/`,  '@type': 'RegistrationsBaseUrl/3.4.0',    comment: 'Package registration metadata.' },
        { '@id': `${base}/v3/search`,         '@type': 'SearchQueryService/3.5.0',      comment: 'Package search.' },
        { '@id': `${base}/v3/search`,         '@type': 'SearchQueryService/3.0.0-beta', comment: '' },
        { '@id': `${base}/v3/search`,         '@type': 'SearchQueryService',             comment: '' },
      ],
    })
    return
  }

  // ── /v3/flatcontainer/{id}/index.json  (список версий) ───────────────────
  const fcListM = p.match(/^\/v3\/flatcontainer\/([^/]+)\/index\.json$/i)
  if (fcListM) {
    const pkg = registry[fcListM[1].toLowerCase()]
    if (!pkg) { json(404, { error: 'not_found', reason: `Package '${fcListM[1]}' not found` }); return }
    json(200, { versions: Object.keys(pkg.versions) })
    return
  }

  // ── /v3/flatcontainer/{id}/{version}/{file}.nupkg  (скачивание) ──────────
  const fcDlM = p.match(/^\/v3\/flatcontainer\/([^/]+)\/([^/]+)\/[^/]+\.nupkg$/i)
  if (fcDlM) {
    const pkg     = registry[fcDlM[1].toLowerCase()]
    const verInfo = pkg?.versions[fcDlM[2]]
    if (!verInfo) { json(404, { error: 'not_found', reason: 'Package or version not found' }); return }
    try {
      const stat = statSync(verInfo.nupkgPath)
      res.writeHead(200, { 'Content-Type': 'application/zip', 'Content-Length': stat.size })
      createReadStream(verInfo.nupkgPath).pipe(res)
    } catch { json(500, { error: 'read_error' }) }
    return
  }

  // ── /v3/registration/{id}/index.json  (метаданные + зависимости) ─────────
  const regM = p.match(/^\/v3\/registration\/([^/]+)\/index\.json$/i)
  if (regM) {
    const lId = regM[1].toLowerCase()
    const pkg = registry[lId]
    if (!pkg) { json(404, { error: 'not_found', reason: `Package '${lId}' not found` }); return }

    const items = Object.entries(pkg.versions).map(([ver, info]) => ({
      '@id':          `${base}/v3/registration/${lId}/${ver}.json`,
      packageContent: `${base}/v3/flatcontainer/${lId}/${ver}/${lId}.${ver}.nupkg`,
      catalogEntry: {
        '@id':     `${base}/v3/catalog0/data/${lId}.${ver}.json`,
        id:        pkg.id,
        version:   ver,
        description: '',
        authors:   '',
        licenseExpression: '',
        requireLicenseAcceptance: false,
        dependencies: info.deps.map(d => ({
          '@id':  `${base}/v3/catalog0/dep/${lId}/${ver}/${d.id.toLowerCase()}.json`,
          id:     d.id,
          range:  d.range,
        })),
      },
    }))

    const versions = Object.keys(pkg.versions)
    json(200, {
      '@id':  `${base}/v3/registration/${lId}/index.json`,
      count:  1,
      items:  [{
        '@id':   `${base}/v3/registration/${lId}/page/0.json`,
        count:   items.length,
        lower:   versions[0]  ?? '',
        upper:   versions.at(-1) ?? '',
        items,
      }],
    })
    return
  }

  // ── /v3/search?q=...&skip=...&take=...  (поиск) ──────────────────────────
  if (p === '/v3/search') {
    const q    = (url.searchParams.get('q')    ?? '').toLowerCase().trim()
    const skip = Math.max(0, parseInt(url.searchParams.get('skip') ?? '0', 10) || 0)
    const take = Math.min(100, Math.max(1, parseInt(url.searchParams.get('take') ?? '20', 10) || 20))

    const all  = Object.values(registry).filter(pkg => !q || pkg.id.toLowerCase().includes(q))
    const page = all.slice(skip, skip + take)

    json(200, {
      totalHits: all.length,
      data: page.map(pkg => {
        const vers   = Object.keys(pkg.versions)
        const latest = vers.at(-1) ?? ''
        return {
          id:          pkg.id,
          version:     latest,
          description: '',
          authors:     [],
          totalDownloads: 0,
          versions:    vers.map(v => ({ version: v, downloads: 0 })),
        }
      }),
    })
    return
  }

  // ── Ping / root ───────────────────────────────────────────────────────────
  if (p === '/' || p === '/-/ping') {
    json(200, { ok: true, packages: pkgCount })
    return
  }

  json(404, { error: 'not_found', path: p })

}).listen(PORT, '0.0.0.0', () => {
  console.log(`[nuget-server] Сервер запущен на порту ${PORT} (все интерфейсы)`)
  console.log(`[nuget-server] В nuget.config: <add key="Local" value="http://<сервер>:${PORT}/v3/index.json" />`)
  console.log(`[nuget-server] Затем: dotnet restore  (Visual Studio обновляет пакеты автоматически)`)
})
