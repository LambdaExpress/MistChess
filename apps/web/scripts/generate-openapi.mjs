import { writeFile } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
import openapiTS, { astToString } from 'openapi-typescript'

const defaultDocument = 'http://localhost:5052/openapi/v1.json'
const documentUrl = process.env.MISTCHESS_OPENAPI_URL ?? defaultDocument
const outputUrl = new URL('../src/api/schema.d.ts', import.meta.url)

const ast = await openapiTS(new URL(documentUrl))
const generated = astToString(ast)
await writeFile(fileURLToPath(outputUrl), generated, 'utf8')
console.log(`Generated src/api/schema.d.ts from ${documentUrl}`)
