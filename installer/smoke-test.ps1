# Run by GitHub Actions after the installer is built (Windows PowerShell 5.1).
# Installs silently, starts the installed app, checks the window and the task list,
# saves screenshots into publish\screenshots, then uninstalls.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing

$shots = Join-Path $PWD 'publish\screenshots'
New-Item -ItemType Directory -Force $shots | Out-Null
function Shot($name) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save((Join-Path $shots "$name.png")); $g.Dispose(); $bmp.Dispose()
}

try { Set-DisplayResolution -Width 1920 -Height 1080 -Force | Out-Null } catch { Write-Host "Resolution not changed: $_" }

# --- Install ---
$setup = Join-Path $PWD 'publish\ProfileRefresher-Setup.exe'
$log = Join-Path $PWD 'publish\install.log'
$p = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"" -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Installer exit code $($p.ExitCode)" }

$dir = Join-Path $env:LOCALAPPDATA 'Programs\ProfileRefresher'
foreach ($f in 'ProfileRefresher.exe', 'extension\manifest.json', 'extension\chats.js', 'unins000.exe') {
    if (!(Test-Path (Join-Path $dir $f))) { throw "Missing after install: $f" }
}
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Browser Profile Refresher.lnk'
if (!(Test-Path $startMenu)) { throw 'Start Menu shortcut missing' }
Write-Host "Installed to $dir"

# --- Start the installed app (with no .NET on the PATH, to prove it brings its own) ---
$env:DOTNET_ROOT = $null
$app = Start-Process (Join-Path $dir 'ProfileRefresher.exe') -PassThru
$deadline = (Get-Date).AddSeconds(40)
do { Start-Sleep 1; $app.Refresh() } until ($app.HasExited -or $app.MainWindowHandle -ne 0 -or (Get-Date) -gt $deadline)
if ($app.HasExited) { throw "App exited at startup (code $($app.ExitCode))" }
if ($app.MainWindowHandle -eq 0) { Shot 'no-window'; throw 'App window did not appear' }
Start-Sleep 4
$app.Refresh()
Write-Host "Window title: $($app.MainWindowTitle)"
if ($app.MainWindowTitle -notlike '*Browser Profile Refresher*') { Shot 'wrong-window'; throw 'Unexpected window title' }
Shot '1-start'

# --- Task list ---
$AE = [System.Windows.Automation.AutomationElement]
$root = $AE::FromHandle($app.MainWindowHandle)
function Find($id) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
$combo = Find 'TaskCombo'
if (!$combo) { throw 'Task list not found' }
$ec = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
$ec.Expand(); Start-Sleep 2
Shot '2-task-list'
$itemCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$items = @($combo.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCond))
$shown = @()
foreach ($i in $items) {
    Write-Host ("  task item: '{0}' offscreen={1} enabled={2}" -f $i.Current.Name, $i.Current.IsOffscreen, $i.Current.IsEnabled)
    if ($i.Current.IsEnabled) { $shown += $i.Current.Name }
}
$expected = 'Refresh one tab', "Post I'm hosting in the global chat", 'Autopilot mode'
if (($shown -join '|') -ne ($expected -join '|')) { throw "Task list is '$($shown -join '|')', expected '$($expected -join '|')'" }

$auto = $items | Where-Object { $_.Current.Name -eq 'Autopilot mode' }
$auto.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
$ec.Collapse(); Start-Sleep 2
Shot '3-autopilot'
foreach ($id in 'ReplyList', 'HostingEveryBox', 'TelegramCheck', 'TabMatchBox', 'StartButton', 'LogList') {
    $e = Find $id
    if (!$e -or $e.Current.IsOffscreen -and $id -in 'StartButton', 'LogList') { throw "Autopilot: '$id' not available" }
}
Write-Host 'Autopilot settings are shown.'

# --- Close and uninstall ---
$app.CloseMainWindow() | Out-Null
if (!$app.WaitForExit(15000)) { $app.Kill(); throw 'App did not close' }
$u = Start-Process (Join-Path $dir 'unins000.exe') -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
if ($u.ExitCode -ne 0) { throw "Uninstaller exit code $($u.ExitCode)" }
Start-Sleep 3
if (Test-Path (Join-Path $dir 'ProfileRefresher.exe')) { throw 'App still there after uninstall' }
Write-Host 'Smoke test passed.'
