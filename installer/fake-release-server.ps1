# Test helper for smoke-test.ps1: serves a fake "latest release" on http://localhost:<port>/
# so the app's Update now flow can be tested end to end without publishing anything.
param([int]$Port, [string]$SetupPath, [string]$Version)
$ErrorActionPreference = 'Stop'
$size = (Get-Item $SetupPath).Length
$hash = (Get-FileHash $SetupPath -Algorithm SHA256).Hash.ToLowerInvariant()
$json = @{
    tag_name = "v$Version"; draft = $false; prerelease = $false
    body = "- Test update from the automated check"
    html_url = 'https://github.com/geoffreymamcer/profile-refresher/releases'
    assets = @(@{ name = 'ProfileRefresher-Setup.exe'; size = $size; digest = "sha256:$hash"
                  browser_download_url = "http://localhost:$Port/setup.exe" })
} | ConvertTo-Json -Depth 5
$l = New-Object System.Net.HttpListener
$l.Prefixes.Add("http://localhost:$Port/")
$l.Start()
while ($l.IsListening) {
    $c = $l.GetContext()
    try {
        if ($c.Request.Url.AbsolutePath -eq '/setup.exe') {
            $c.Response.ContentType = 'application/octet-stream'
            $c.Response.ContentLength64 = $size
            $f = [System.IO.File]::OpenRead($SetupPath); $f.CopyTo($c.Response.OutputStream); $f.Close()
        } else {
            $b = [Text.Encoding]::UTF8.GetBytes($json)
            $c.Response.ContentType = 'application/json'
            $c.Response.ContentLength64 = $b.Length
            $c.Response.OutputStream.Write($b, 0, $b.Length)
        }
    } finally { $c.Response.Close() }
}
