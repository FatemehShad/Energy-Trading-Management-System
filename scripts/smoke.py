"""Exercise API persistence, positions and actual Kafka delivery against local Compose services."""
import json
import os
import subprocess
import time
import urllib.error
import urllib.request
import uuid

base = os.environ.get('API_URL', 'http://localhost:8080')

def request(method, path, body=None, expected=200):
    headers = {'Content-Type': 'application/json'}
    if os.environ.get('ApiKey'):
        headers['X-Api-Key'] = os.environ['ApiKey']
    req = urllib.request.Request(base + path, method=method, headers=headers,
                                 data=json.dumps(body).encode() if body is not None else None)
    try:
        response = urllib.request.urlopen(req, timeout=10)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        data = response.read()
        assert response.status == expected, (response.status, data)
        return json.loads(data) if data else None

for attempt in range(30):
    try:
        request('GET', '/health/ready')
        break
    except (OSError, AssertionError):
        if attempt == 29:
            raise
        time.sleep(1)

portfolio = str(uuid.uuid4())
body = dict(clientTradeId=portfolio, portfolio=portfolio, product='POWER-DE', side='Buy',
            quantityMwh=10, pricePerMwh=50, deliveryStart='2027-01-01T00:00:00Z',
            deliveryEnd='2027-01-01T01:00:00Z')
trade = request('POST', '/api/trades', body, 201)
request('POST', '/api/trades', body, 409)
positions = request('GET', '/api/positions?portfolio=' + portfolio)
assert positions[0]['netQuantityMwh'] == 10 and positions[0]['netCashFlow'] == -500
request('POST', '/api/trades/' + trade['id'] + '/cancel')
assert request('GET', '/api/positions?portfolio=' + portfolio) == []
request('POST', '/api/market-data', dict(product=portfolio, pricePerMwh=-5,
        observedAt='2026-10-08T00:00:00Z'), 201)
assert request('GET', '/api/market-data/latest?product=' + portfolio)['pricePerMwh'] == -5

# Kafka console consumer uses timeout exit code 1 after consuming available records.
# Require the exact lifecycle events regardless of its documented timeout status.
result = subprocess.run(['docker', 'compose', 'exec', '-T', 'kafka',
    '/opt/kafka/bin/kafka-console-consumer.sh', '--bootstrap-server', 'kafka:29092',
    '--topic', 'energy.trades.v1', '--from-beginning', '--timeout-ms', '15000'],
    capture_output=True, text=True, timeout=45)
assert result.returncode in (0, 1), result.stderr
messages = [json.loads(line) for line in result.stdout.splitlines() if line.startswith('{')]
events = [m['eventType'] for m in messages if m.get('trade', {}).get('Id') == trade['id']]
assert 'TradeCreated' in events and 'TradeCancelled' in events, (events, result.stderr)
assert events.index('TradeCreated') < events.index('TradeCancelled'), events
print('PASS: trade persistence, duplicate rejection, position netting, cancellation, market quote and Kafka lifecycle delivery')
