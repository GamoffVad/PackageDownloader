/**
 * Локальный npm-реестр — минималистичная реализация npm registry API.
 * Использует только встроенные модули Node.js (нет внешних зависимостей).
 *
 * Env vars:
 *   NPM_REGISTRY_PORT  — порт (по умолчанию 4873)
 *   NPM_PACKAGES_DIR   — папка с пакетами (.tgz), сканируется рекурсивно;
 *                        поддерживается раскладка <имя>/<версия>/<файл>.tgz
 *
 * Сервер слушает на всех интерфейсах (0.0.0.0), а ссылки на tarball-ы
 * формируются по заголовку Host из запроса — поэтому реестр доступен
 * другим машинам в корпоративной сети. На клиенте достаточно один раз:
 *   npm config set registry http://<имя-сервера>:<порт>/
 *   npm i <название-пакета>
 */

import { createServer } from 'node:http'
import { createReadStream, readdirSync, statSync, readFileSync } from 'node:fs'
import { gunzipSync } from 'node:zlib'
import { join, extname, basename } from 'node:path'
import { createHash } from 'node:crypto'

const PORT = parseInt(process.env.NPM_REGISTRY_PORT ?? '4873', 10)
const DIR  = process.env.NPM_PACKAGES_DIR ?? ''

// Как часто перечитывать папку с пакетами (мс) — чтобы новые загрузки
// подхватывались без перезапуска сервера.
const RESCAN_MS = 5000

// Карта «имя файла .tgz → абсолютный путь» (пакеты лежат во вложенных папках).
let tgzPaths = {}

// Разбор .tgz дорогой (gunzip + sha512), поэтому кэшируем по mtime/размеру.
const metaCache = new Map()

// ── Минимальный tar-парсер (ustar / pax) ─────────────────────────────────
function parseTar(buf) {
  const entries = []
  let offset = 0

  while (offset + 512 <= buf.length) {
    const header = buf.subarray(offset, offset + 512)

    // Два нулевых блока = конец архива
    if (header.every(b => b === 0)) break

    const prefix = header.subarray(345, 500).toString('utf8').replace(/\0/g, '')
    const name   = header.subarray(0, 100).toString('utf8').replace(/\0/g, '')
    const full   = prefix ? `${prefix}/${name}` : name
    const size   = parseInt(header.subarray(124, 136).toString('utf8').replace(/\0/g, '').trim(), 8) || 0

    offset += 512
    entries.push({ name: full, data: buf.subarray(offset, offset + size) })
    offset += Math.ceil(size / 512) * 512
  }

  return entries
}

// ── Читаем package.json из .tgz ──────────────────────────────────────────
function readPkgJson(tgzPath) {
  try {
    const entries = parseTar(gunzipSync(readFileSync(tgzPath)))
    const entry   = entries.find(e =>
      e.name === 'package/package.json' ||
      e.name === './package/package.json' ||
      (e.name.startsWith('package/') && e.name.endsWith('package.json'))
    )
    return entry ? JSON.parse(entry.data.toString('utf8')) : null
  } catch {
    return null
  }
}

// ── Контрольные суммы файла ──────────────────────────────────────────────
function hashFile(filePath) {
  const buf = readFileSync(filePath)
  return {
    shasum:    createHash('sha1').update(buf).digest('hex'),
    integrity: 'sha512-' + createHash('sha512').update(buf).digest('base64'),
  }
}

// ── Рекурсивный поиск всех .tgz в папке (и подпапках) ────────────────────
function walkTgz(dir, out = []) {
  let entries
  try { entries = readdirSync(dir, { withFileTypes: true }) } catch { return out }
  for (const e of entries) {
    const p = join(dir, e.name)
    if (e.isDirectory()) walkTgz(p, out)
    else if (extname(e.name).toLowerCase() === '.tgz') out.push(p)
  }
  return out
}

// ── Загрузить все пакеты из папки ─────────────────────────────────────────
function buildRegistry(dir) {
  const reg   = {}
  const paths = {}
  if (!dir) { tgzPaths = paths; return reg }

  for (const filePath of walkTgz(dir)) {
    let stat
    try { stat = statSync(filePath) } catch { continue }

    // Распаковываем только новые/изменившиеся файлы.
    const cached = metaCache.get(filePath)
    let meta
    if (cached && cached.mtimeMs === stat.mtimeMs && cached.size === stat.size) {
      meta = cached.meta
    } else {
      const pkg = readPkgJson(filePath)
      meta = pkg?.name && pkg?.version ? { ...pkg, dist: hashFile(filePath) } : null
      metaCache.set(filePath, { mtimeMs: stat.mtimeMs, size: stat.size, meta })
    }
    if (!meta) continue

    const { name, version } = meta
    const file = basename(filePath)
    paths[file] = filePath

    if (!reg[name]) reg[name] = { name, versions: {}, 'dist-tags': {} }

    // Имя tarball-файла запоминаем; абсолютный URL формируем при запросе
    // по фактическому хосту (Host header), чтобы работало по сети.
    reg[name].versions[version] = { ...meta, _tgz: file }

    // latest — максимальная версия (пред-релизы младше релизов).
    const current = reg[name]['dist-tags'].latest
    if (!current || compareVersions(version, current) > 0) {
      reg[name]['dist-tags'].latest = version
    }
  }

  tgzPaths = paths
  return reg
}

// Сравнение semver: major.minor.patch, затем пред-релиз (он младше релиза).
function compareVersions(a, b) {
  const [ra, ...preA] = a.split('-')
  const [rb, ...preB] = b.split('-')
  const pa = ra.split('.').map(Number)
  const pb = rb.split('.').map(Number)
  for (let i = 0; i < 3; i++) {
    const diff = (pa[i] ?? 0) - (pb[i] ?? 0)
    if (diff !== 0) return diff
  }
  const sa = preA.join('-'), sb = preB.join('-')
  if (!sa && !sb) return 0
  if (!sa) return 1
  if (!sb) return -1
  return sa.localeCompare(sb)
}

// ── Метаданные пакета с абсолютными tarball-URL для конкретного хоста ──────
function packageDoc(pkg, host) {
  const out = { name: pkg.name, 'dist-tags': pkg['dist-tags'], versions: {} }
  for (const [version, meta] of Object.entries(pkg.versions)) {
    const { _tgz, ...rest } = meta
    out.versions[version] = {
      ...rest,
      dist: {
        ...rest.dist,
        tarball: `http://${host}/${encodeURIComponent(pkg.name)}/-/${encodeURIComponent(_tgz)}`,
      },
    }
  }
  return out
}

// ── Строим реестр ─────────────────────────────────────────────────────────
let registry = {}
let pkgCount = 0
let lastScan = 0

/// Перечитывает папку, если прошло больше RESCAN_MS с прошлого раза.
function rescanIfStale() {
  if (Date.now() - lastScan < RESCAN_MS) return
  lastScan = Date.now()
  const before = pkgCount
  registry = buildRegistry(DIR)
  pkgCount = Object.keys(registry).length
  if (before !== pkgCount) console.log(`[npm-registry] Пакетов в папке: ${pkgCount}`)
}

rescanIfStale()

console.log(`[npm-registry] Папка: ${DIR || '(не задана)'}`)
console.log(`[npm-registry] Загружено пакетов: ${pkgCount}`)
console.log(`[npm-registry] Запуск на порту ${PORT}…`)

// ── HTTP-сервер ───────────────────────────────────────────────────────────
createServer((req, res) => {
  const host  = req.headers.host ?? `localhost:${PORT}`
  const url   = new URL(req.url, `http://${host}`)
  const parts = url.pathname.split('/').filter(Boolean)

  // Лог каждого запроса (в реальном времени, как в verdaccio)
  const startedAt = Date.now()
  res.on('finish', () => {
    const t  = new Date().toTimeString().slice(0, 8)
    const ms = Date.now() - startedAt
    const ip = (req.socket.remoteAddress ?? '').replace('::ffff:', '') || '-'
    console.log(`[${t}] ${req.method} ${decodeURIComponent(url.pathname)} → ${res.statusCode} (${ms}ms, ${ip})`)
  })

  const json = (code, data) => {
    const body = JSON.stringify(data)
    res.writeHead(code, { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) })
    res.end(body)
  }

  // Подхватываем пакеты, скачанные уже после запуска сервера.
  rescanIfStale()

  // ── Ping / root ─────────────────────────────────────────────────────────
  if (url.pathname === '/' || url.pathname === '/-/ping') {
    json(200, { ok: true, packages: pkgCount })
    return
  }

  // ── Tarball: GET /:name/-/:filename.tgz ────────────────────────────────
  // npm запрашивает /pkgname/-/pkgname-1.0.0.tgz
  const lastTwo = parts.slice(-2)
  if (lastTwo[0] === '-' && lastTwo[1]?.endsWith('.tgz')) {
    const filename = decodeURIComponent(lastTwo[1])
    const filePath = tgzPaths[filename]
    try {
      if (!filePath) throw new Error('unknown')
      const stat = statSync(filePath)
      res.writeHead(200, {
        'Content-Type':   'application/octet-stream',
        'Content-Length': stat.size,
      })
      createReadStream(filePath).pipe(res)
    } catch {
      json(404, { error: 'not_found', reason: `file '${filename}' not found` })
    }
    return
  }

  // ── Метаданные пакета: GET /:name или GET /@scope/:name ──────────────────
  const pkgName = parts[0]?.startsWith('%40') || parts[0]?.startsWith('@')
    ? `${decodeURIComponent(parts[0])}/${decodeURIComponent(parts[1] ?? '')}`
    : decodeURIComponent(parts[0] ?? '')

  const pkg = registry[pkgName]
  if (!pkg) {
    json(404, { error: 'not_found', reason: `package '${pkgName}' not found` })
    return
  }

  json(200, packageDoc(pkg, host))
}).listen(PORT, () => {
  console.log(`[npm-registry] Сервер запущен на порту ${PORT} (все интерфейсы)`)
  console.log(`[npm-registry] На клиенте: npm config set registry http://<сервер>:${PORT}/`)
  console.log(`[npm-registry] Затем установка: npm i <название-пакета>`)
})
