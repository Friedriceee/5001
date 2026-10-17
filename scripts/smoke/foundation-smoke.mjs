import http from 'http'
import https from 'https'

function get(url) {
  return new Promise((resolve, reject) => {
    const client = url.protocol === 'https:' ? https : http
    const request = client.get(url, response => {
      let body = ''
      response.setEncoding('utf8')
      response.on('data', chunk => { body += chunk })
      response.on('end', () => resolve({
        status: response.statusCode || 0,
        headers: response.headers,
        body,
      }))
    })
    request.on('error', reject)
    request.setTimeout(5000, () => request.destroy(new Error(`Timed out calling ${url}`)))
  })
}

async function main() {
  const gateway = process.env.JIKEDA_GATEWAY_URL || 'http://127.0.0.1:5000'

  const parsedGateway = new URL(gateway)
  if (!['127.0.0.1', 'localhost'].includes(parsedGateway.hostname)) {
    throw new Error('Foundation smoke test is restricted to a local gateway.')
  }

  const checks = [
    ['gateway', '/health'],
    ['identity', '/health/identity'],
    ['catalog', '/health/catalog'],
    ['order', '/health/order'],
    ['fulfilment', '/health/fulfilment'],
  ]

  for (const [service, path] of checks) {
    const response = await get(new URL(path, gateway))
    if (response.status < 200 || response.status >= 300) {
      throw new Error(`${service} health failed: HTTP ${response.status}: ${response.body}`)
    }

    const correlationId = response.headers['x-correlation-id']
    if (!correlationId) {
      throw new Error(`${service} health response is missing X-Correlation-ID.`)
    }

    console.log(`${service}: healthy (${correlationId})`)
  }

  console.log('Microservice foundation smoke test passed.')
}

main().catch(error => {
  console.error(error)
  process.exitCode = 1
})
