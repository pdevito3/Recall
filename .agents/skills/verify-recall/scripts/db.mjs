import { existsSync } from 'node:fs'
import path from 'node:path'
import { DatabaseSync } from 'node:sqlite'

const DATA_FILES = { appdb: 'recall.db', bffdb: 'bff.db' }

const ident = (name) => `"${name.replaceAll('"', '""')}"`

// JSON cannot hold each SQLite value: an integer can be larger than a double, and a session ticket is a blob.
const encode = (value) => {
  if (typeof value === 'bigint') return { int: String(value) }
  if (value instanceof Uint8Array) return { blob: Buffer.from(value).toString('base64') }
  return value
}
const decode = (value) => {
  if (value?.int !== undefined) return BigInt(value.int)
  if (value?.blob !== undefined) return new Uint8Array(Buffer.from(value.blob, 'base64'))
  return value
}

function open(file) {
  const db = new DatabaseSync(file)
  db.exec('PRAGMA busy_timeout = 30000')
  return db
}

function tablesOf(db, database) {
  const names = db.prepare(`SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY name`).all()
  const tables = names.map(({ name }) => {
    const info = db.prepare(`PRAGMA table_info(${ident(name)})`).all()
    return {
      name,
      columns: info.map((column) => column.name),
      key: info.filter((column) => column.pk > 0).sort((a, b) => a.pk - b.pk).map((column) => column.name),
    }
  })
  const keyless = tables.filter((table) => !table.key.length)
  if (keyless.length) {
    throw new Error(`db: ${database} has tables with no primary key, so a restore cannot find their rows: ${keyless.map((table) => table.name).join(', ')}`)
  }
  return tables
}

function rowsOf(db, table) {
  const statement = db.prepare(`SELECT * FROM ${ident(table.name)}`)
  statement.setReadBigInts(true)
  const rows = {}
  for (const row of statement.iterate()) {
    rows[JSON.stringify(table.key.map((column) => encode(row[column])))] = JSON.stringify(table.columns.map((column) => encode(row[column])))
  }
  return rows
}

export function snapshot(dataDirectory) {
  const databases = {}
  for (const [database, file] of Object.entries(DATA_FILES)) {
    const filePath = path.join(dataDirectory, file)
    if (!existsSync(filePath)) continue
    const db = open(filePath)
    try {
      const tables = tablesOf(db, database)
      databases[database] = { tables, rows: Object.fromEntries(tables.map((table) => [table.name, rowsOf(db, table)])) }
    } finally {
      db.close()
    }
  }
  return databases
}

function differences(before, after) {
  const diffs = []
  for (const [database, { tables, rows }] of Object.entries(before)) {
    for (const table of tables) {
      const was = rows[table.name]
      const now = after[database]?.rows[table.name] ?? {}
      const added = Object.keys(now).filter((key) => !(key in was))
      const changed = Object.keys(was).filter((key) => key in now && now[key] !== was[key])
      const deleted = Object.keys(was).filter((key) => !(key in now))
      if (added.length + changed.length + deleted.length) diffs.push({ database, table, added, changed, deleted })
    }
  }
  return diffs
}

// With foreign keys off, no check looks at the order of the statements and no cascade runs.
function applyDiffs(db, rows, diffs) {
  db.exec('PRAGMA foreign_keys = OFF')
  db.exec('BEGIN IMMEDIATE')
  try {
    const triggers = db.prepare(`SELECT name, sql FROM sqlite_master WHERE type = 'trigger'`).all()
    for (const { name } of triggers) db.exec(`DROP TRIGGER ${ident(name)}`)
    for (const { table, added, changed, deleted } of diffs) {
      const remove = db.prepare(`DELETE FROM ${ident(table.name)} WHERE ${table.key.map((column) => `${ident(column)} = ?`).join(' AND ')}`)
      for (const key of [...added, ...changed]) remove.run(...JSON.parse(key).map(decode))
      const insert = db.prepare(`INSERT INTO ${ident(table.name)} (${table.columns.map(ident).join(', ')}) VALUES (${table.columns.map(() => '?').join(', ')})`)
      for (const key of [...changed, ...deleted]) insert.run(...JSON.parse(rows[table.name][key]).map(decode))
    }
    for (const { sql } of triggers) db.exec(sql)
    db.exec('COMMIT')
  } catch (e) {
    db.exec('ROLLBACK')
    throw e
  }
}

const changeCountsByTable = (diffs) =>
  Object.fromEntries(diffs.map(({ database, table, added, changed, deleted }) =>
    [`${database}.${table.name}`, { added: added.length, changed: changed.length, deleted: deleted.length }]))

export class RestoreError extends Error {
  constructor(cause, changes) {
    super(cause.message, { cause })
    this.changes = changes
  }
}

export function restore(dataDirectory, before) {
  const diffs = differences(before, snapshot(dataDirectory))
  const changes = changeCountsByTable(diffs)
  try {
    for (const [database, tableDiffs] of Map.groupBy(diffs, (diff) => diff.database)) {
      const db = open(path.join(dataDirectory, DATA_FILES[database]))
      try {
        applyDiffs(db, before[database].rows, tableDiffs)
      } finally {
        db.close()
      }
    }
    const left = differences(before, snapshot(dataDirectory))
    if (left.length) {
      throw new Error(`db: the restore left these tables different from the snapshot: ${Object.keys(changeCountsByTable(left)).join(', ')}`)
    }
  } catch (e) {
    throw new RestoreError(e, changes)
  }
  return changes
}
