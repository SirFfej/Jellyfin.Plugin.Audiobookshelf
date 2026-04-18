$headers = @{
    'Authorization' = 'Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJrZXlJZCI6IjM0MDliZTg5LWNjMzgtNGJjMS1hMWE1LTBmODMxZTMxZmE2OSIsIm5hbWUiOiJQbHVnaW4tVGVzdGluZyIsInR5cGUiOiJhcGkiLCJpYXQiOjE3NzYzOTE1Mjl9.efjpqzPytyLebpdmBL9_t26bucEvsmSnREC32zsnQ_E'
}

# Fetch AudioBooks library items
$response = Invoke-RestMethod -Uri 'http://192.168.86.229:13378/api/libraries/6090c430-fa60-4003-8bf3-337d3bd7b037/items?limit=5' -Headers $headers
$response | ConvertTo-Json -Depth 10 | Out-File "test-data/abs-audiobooks-library-items.json" -Encoding UTF8

Write-Host "Fetched $($response.results.Count) items"
Write-Host "First item: $($response.results[0].media.metadata.title)"
