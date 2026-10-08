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
body = dict(clientTradeId=portfolio, portfolio=portfolio, product='POWER-DE', commodity='Electricity', currency='EUR', side='Buy',
            quantityMwh=10, pricePerMwh=50, deliveryStart='2027-01-01T00:00:00Z',
            deliveryEnd='2027-01-01T01:00:00Z')
trade = request('POST', '/api/trades', body, 201)
request('POST', '/api/trades', body, 409)
gas = request('POST', '/api/trades', dict(body, clientTradeId=portfolio + '-gas',
    product='GAS-TTF', commodity='Gas', side='Sell', quantityMwh=20, pricePerMwh=30), 201)
positions = request('GET', '/api/positions?portfolio=' + portfolio)
assert len(positions) == 2
power = next(p for p in positions if p['commodity'] == 'Electricity')
gas_position = next(p for p in positions if p['commodity'] == 'Gas')
assert power['netQuantityMwh'] == 10 and power['netCashFlow'] == -500
assert gas_position['netQuantityMwh'] == -20 and gas_position['netCashFlow'] == 600
request('POST', '/api/trades/' + trade['id'] + '/cancel')
assert len(request('GET', '/api/positions?portfolio=' + portfolio)) == 1
request('POST', '/api/trades/' + gas['id'] + '/cancel')
assert request('GET', '/api/positions?portfolio=' + portfolio) == []
request('POST', '/api/market-data', dict(product=portfolio, commodity='Gas', currency='EUR', pricePerMwh=-5,
        observedAt='2026-10-08T00:00:00Z'), 201)
assert request('GET', '/api/market-data/latest?commodity=Gas&currency=EUR&product=' + portfolio)['pricePerMwh'] == -5

# Kafka console consumer uses timeout exit code 1 after consuming available records.
# Require the exact lifecycle events regardless of its documented timeout status.
result = subprocess.run(['docker', 'compose', 'exec', '-T', 'kafka',
    '/opt/kafka/bin/kafka-console-consumer.sh', '--bootstrap-server', 'kafka:29092',
    '--topic', 'energy.trades.v1', '--from-beginning', '--timeout-ms', '15000'],
    capture_output=True, text=True, timeout=45)
assert result.returncode in (0, 1), result.stderr
messages = [json.loads(line) for line in result.stdout.splitlines() if line.startswith('{')]
for booked in (trade, gas):
    delivered = [m for m in messages if m.get('trade', {}).get('id') == booked['id']]
    events = [m['eventType'] for m in delivered]
    assert 'TradeCreated' in events and 'TradeCancelled' in events, (events, result.stderr)
    assert events.index('TradeCreated') < events.index('TradeCancelled'), events
    assert all(m['schemaVersion'] == 2 and m['trade']['commodity'] == booked['commodity'] for m in delivered)
print('PASS: electricity and gas booking, duplicate rejection, separate positions, cancellation, market quote and ordered Kafka lifecycle delivery')
