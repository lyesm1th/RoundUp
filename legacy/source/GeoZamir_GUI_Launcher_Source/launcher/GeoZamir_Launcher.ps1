# GeoZamir Launcher · Windows PowerShell 5.1
# Default: solo + local updates only. No remote URLs, no login, no telemetry.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$configPath = Join-Path $root 'launcher.config.json'
$versionPath = Join-Path $root 'current-version.json'
$logPath = Join-Path $PSScriptRoot 'launcher.log'
$app = Join-Path $root 'GeoZamir.exe'

function Write-LauncherLog([string]$message) {
    try {
        $line = '[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '] ' + $message
        Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8 -ErrorAction Stop
    } catch { }
}

function Assert-FileName([string]$name) {
    if ([string]::IsNullOrWhiteSpace($name) -or $name -ne [IO.Path]::GetFileName($name) -or
        $name -match '[\\/]' -or $name -match '\.\.') {
        throw 'Недопустиме ім’я файлу оновлення'
    }
}

function Read-LocalUpdate($config) {
    if ($config.mode -ne 'solo' -or $config.identity.provider -ne 'solo' -or
        $config.identity.remoteEnabled -ne $false -or $config.sync.enabled -ne $false -or
        $null -ne $config.sync.endpoint) {
        throw 'Цей лаунчер підтримує лише особистий локальний режим'
    }
    $updates = $config.updates
    if ($null -ne $updates.remoteManifestUrl) {
        throw 'Віддалений канал ще не підключений; URL має бути порожнім'
    }
    if ($updates.mode -ne 'local') { return $null }
    Assert-FileName ([string]$updates.directory)
    Assert-FileName ([string]$updates.manifest)
    $folder = Join-Path $root ([string]$updates.directory)
    $manifestPath = Join-Path $folder ([string]$updates.manifest)
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { return $null }
    $manifest = Get-Content -LiteralPath $manifestPath -Encoding UTF8 -Raw | ConvertFrom-Json
    Assert-FileName ([string]$manifest.package)
    $packagePath = Join-Path $folder ([string]$manifest.package)
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) { throw 'ZIP з оновленням відсутній' }
    if ([string]$manifest.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        [string]$manifest.exeSha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'Контрольні суми SHA-256 мають містити рівно 64 символи'
    }
    $targetVersion = [Version]([string]$manifest.version)
    $currentVersion = [Version]'0.0.0'
    if (Test-Path -LiteralPath $versionPath) {
        $current = Get-Content -LiteralPath $versionPath -Encoding UTF8 -Raw | ConvertFrom-Json
        $currentVersion = [Version]([string]$current.version)
    }
    if ($targetVersion -le $currentVersion) { return $null }
    return [pscustomobject]@{
        PackagePath = $packagePath
        TargetVersion = [string]$manifest.version
        CurrentVersion = $currentVersion.ToString()
        PackageHash = [string]$manifest.sha256
        ExeHash = [string]$manifest.exeSha256
    }
}

function Install-LocalUpdate($item) {
    if ($null -eq $item) { return }
    if (Get-Process -Name 'GeoZamir' -ErrorAction SilentlyContinue) {
        throw 'Закрийте всі відкриті вікна GeoZamir перед оновленням.'
    }
    $hash = (Get-FileHash -LiteralPath $item.PackagePath -Algorithm SHA256).Hash
    if ($hash -ine $item.PackageHash) { throw 'Контрольна сума ZIP не збігається' }

    $stagingRoot = Join-Path $root '.launcher-staging'
    $staging = Join-Path $stagingRoot ([Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $staging -Force
    $newExe = Join-Path $staging 'GeoZamir.exe'

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($item.PackagePath)
    try {
        $entry = $zip.GetEntry('GeoZamir.exe')
        if ($null -eq $entry) { throw 'ZIP не містить GeoZamir.exe у корені' }
        if ($entry.Length -gt 100MB -or $entry.Length -lt 100KB) { throw 'Підозрілий розмір програми у ZIP' }
        $inputStream = $entry.Open()
        try {
            $outputStream = [IO.File]::Create($newExe)
            try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
        } finally { $inputStream.Dispose() }
    } finally { $zip.Dispose() }

    if ((Get-FileHash -LiteralPath $newExe -Algorithm SHA256).Hash -ine $item.ExeHash) {
        throw 'Контрольна сума GeoZamir.exe не збігається'
    }
    $reader = [IO.File]::OpenRead($newExe)
    try {
        if ($reader.ReadByte() -ne 77 -or $reader.ReadByte() -ne 90) { throw 'Файл оновлення не є Windows EXE' }
    } finally { $reader.Dispose() }

    $backupDir = Join-Path $root '.launcher-backup'
    $null = New-Item -ItemType Directory -Path $backupDir -Force
    $backup = Join-Path $backupDir ('GeoZamir-' + $item.CurrentVersion + '-' + [Guid]::NewGuid().ToString('N') + '.exe')
    $hadPrevious = Test-Path -LiteralPath $app -PathType Leaf
    $movedPrevious = $false
    try {
        if ($hadPrevious) {
            Move-Item -LiteralPath $app -Destination $backup -ErrorAction Stop
            $movedPrevious = $true
        }
        Move-Item -LiteralPath $newExe -Destination $app -ErrorAction Stop
        $newVersionFile = Join-Path $staging 'current-version.json'
        @{version = $item.TargetVersion; channel = 'stable'} | ConvertTo-Json | Set-Content -LiteralPath $newVersionFile -Encoding UTF8
        Move-Item -LiteralPath $newVersionFile -Destination $versionPath -Force -ErrorAction Stop
        Write-LauncherLog ('Оновлено до GeoZamir ' + $item.TargetVersion + '. Попередню EXE збережено: ' + $backup)
    } catch {
        # Restore only the exact executable file we just moved; never touch GeoZamir_Data.
        if ($movedPrevious -and (Test-Path -LiteralPath $backup)) {
            if (Test-Path -LiteralPath $app) {
                Move-Item -LiteralPath $app -Destination (Join-Path $staging 'failed-new-version.exe') -ErrorAction SilentlyContinue
            }
            if (-not (Test-Path -LiteralPath $app)) {
                Move-Item -LiteralPath $backup -Destination $app -ErrorAction Stop
            }
        }
        throw
    }
}

# The launcher window is always shown; checking for an update never installs
# anything until the user chooses the "Оновити" action.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

function Color([string]$hex) { return [System.Drawing.ColorTranslator]::FromHtml($hex) }
function Make-Label([string]$value,[int]$x,[int]$y,[int]$w,[int]$h,
                    [int]$size=10,[bool]$bold=$false,[string]$hex='#d9eee7') {
    $label = New-Object System.Windows.Forms.Label
    $label.Text = $value
    $label.Location = New-Object System.Drawing.Point($x,$y)
    $label.Size = New-Object System.Drawing.Size($w,$h)
    $label.ForeColor = Color $hex
    $weight = if ($bold) {[System.Drawing.FontStyle]::Bold} else {[System.Drawing.FontStyle]::Regular}
    $label.Font = New-Object System.Drawing.Font('Segoe UI',$size,$weight)
    $label.AutoEllipsis = $true
    return $label
}

$script:Form = New-Object System.Windows.Forms.Form
$script:Form.Text = 'ГеоЗамір — лаунчер'
$script:Form.ClientSize = New-Object System.Drawing.Size(674,467)
$script:Form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
$script:Form.MaximizeBox = $false
$script:Form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
$script:Form.BackColor = Color '#10282d'
$script:Form.Font = New-Object System.Drawing.Font('Segoe UI',10)

$accent = New-Object System.Windows.Forms.Panel
$accent.Location = New-Object System.Drawing.Point(0,0)
$accent.Size = New-Object System.Drawing.Size(674,6)
$accent.BackColor = Color '#78e0af'
$script:Form.Controls.Add($accent)

$logoPath = Join-Path $PSScriptRoot 'assets\geozamir-mark.png'
$iconPath = Join-Path $PSScriptRoot 'assets\geozamir-mark.ico'
if (Test-Path -LiteralPath $iconPath) {
    try { $script:Form.Icon = New-Object System.Drawing.Icon($iconPath) } catch { }
}
$logo = New-Object System.Windows.Forms.PictureBox
$logo.Location = New-Object System.Drawing.Point(36,42)
$logo.Size = New-Object System.Drawing.Size(101,101)
$logo.SizeMode = [System.Windows.Forms.PictureBoxSizeMode]::Zoom
if (Test-Path -LiteralPath $logoPath) {
    try { $logo.Image = [System.Drawing.Image]::FromFile($logoPath) } catch { }
}
$script:Form.Controls.Add($logo)
$script:Form.Controls.Add((Make-Label 'ГеоЗамір' 158 43 460 53 29 $true '#f4fff9'))
$script:Form.Controls.Add((Make-Label 'Інтерактивна карта, що пам’ятає кожну забудову' 159 101 465 30 11 $false '#b8d6d0'))
$script:Form.Controls.Add((Make-Label '●  ОСОБИСТИЙ РЕЖИМ  ·  ДАНІ НА ВАШОМУ ПК' 159 141 480 25 9 $true '#7bddb0'))

$box = New-Object System.Windows.Forms.Panel
$box.Location = New-Object System.Drawing.Point(36,203)
$box.Size = New-Object System.Drawing.Size(602,121)
$box.BackColor = Color '#1a3a3f'
$script:Form.Controls.Add($box)
$script:VersionLabel = Make-Label 'Встановлена версія: …' 21 16 555 25 11 $true '#f3fff8'
$script:StatusLabel = Make-Label 'Перевірка оновлень…' 21 49 557 28 12 $true '#91edba'
$script:DetailLabel = Make-Label 'Локальний канал оновлень' 21 83 557 23 9 $false '#9fc2bb'
$box.Controls.Add($script:VersionLabel)
$box.Controls.Add($script:StatusLabel)
$box.Controls.Add($script:DetailLabel)

$script:Primary = New-Object System.Windows.Forms.Button
$script:Primary.Location = New-Object System.Drawing.Point(36,352)
$script:Primary.Size = New-Object System.Drawing.Size(426,62)
$script:Primary.Text = 'Перевіряємо…'
$script:Primary.Enabled = $false
$script:Primary.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$script:Primary.FlatAppearance.BorderSize = 0
$script:Primary.BackColor = Color '#8ee3b9'
$script:Primary.ForeColor = Color '#123c37'
$script:Primary.Font = New-Object System.Drawing.Font('Segoe UI',14,[System.Drawing.FontStyle]::Bold)
$script:Primary.Cursor = [System.Windows.Forms.Cursors]::Hand
$script:Form.Controls.Add($script:Primary)

$recheck = New-Object System.Windows.Forms.Button
$recheck.Location = New-Object System.Drawing.Point(480,352)
$recheck.Size = New-Object System.Drawing.Size(158,62)
$recheck.Text = '↻  Перевірити'
$recheck.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$recheck.FlatAppearance.BorderColor = Color '#4a7772'
$recheck.BackColor = Color '#19393d'
$recheck.ForeColor = Color '#dbf5e8'
$recheck.Cursor = [System.Windows.Forms.Cursors]::Hand
$script:Form.Controls.Add($recheck)
$script:Form.Controls.Add((Make-Label 'Оновлення перевіряються лише локально · хмару не підключено' 37 435 600 22 9 $false '#83a6a0'))

$script:Candidate = $null
function Refresh-LauncherState {
    $script:Candidate = $null
    $script:Primary.Enabled = $false
    $script:StatusLabel.ForeColor = Color '#91edba'
    $script:StatusLabel.Text = 'Перевіряємо наявність оновлень…'
    $script:DetailLabel.Text = 'Перевірка локальної папки Updates'
    [System.Windows.Forms.Application]::DoEvents()
    try {
        $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $script:Candidate = Read-LocalUpdate $config
        $current = Get-Content -LiteralPath $versionPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $script:VersionLabel.Text = 'Встановлена версія: ' + $current.version
        if ($null -eq $script:Candidate) {
            $script:StatusLabel.Text = 'Програма готова до запуску'
            $script:DetailLabel.Text = 'Новіших збірок у локальному каналі немає'
            $script:Primary.Text = '▶  Запустити ГеоЗамір'
        } else {
            $script:StatusLabel.Text = 'Доступна нова версія ' + $script:Candidate.TargetVersion
            $script:DetailLabel.Text = 'Натисніть «Оновити», щоб встановити перевірений пакет'
            $script:Primary.Text = '⬇  Оновити до ' + $script:Candidate.TargetVersion
        }
    } catch {
        $script:StatusLabel.Text = 'Не вдалося перевірити оновлення'
        $script:StatusLabel.ForeColor = Color '#ffb2a5'
        $script:DetailLabel.Text = $_.Exception.Message
        $script:Primary.Text = '▶  Запустити встановлену версію'
        Write-LauncherLog ('Помилка перевірки: ' + $_.Exception.Message)
    }
    $script:Primary.Enabled = Test-Path -LiteralPath $app -PathType Leaf
}

$recheck.Add_Click({ Refresh-LauncherState })
$script:Primary.Add_Click({
    if ($null -ne $script:Candidate) {
        $item = $script:Candidate
        $script:Primary.Enabled = $false
        $script:Primary.Text = 'Встановлення оновлення…'
        $script:StatusLabel.Text = 'Перевіряємо файли та оновлюємо програму'
        [System.Windows.Forms.Application]::DoEvents()
        try {
            Install-LocalUpdate $item
            Refresh-LauncherState
            $script:StatusLabel.Text = 'Оновлено до версії ' + $item.TargetVersion
            $script:DetailLabel.Text = 'Усе готово. Можна запускати програму.'
        } catch {
            Write-LauncherLog ('Помилка встановлення: ' + $_.Exception.Message)
            $script:Candidate = $null
            $script:StatusLabel.Text = 'Не вдалося встановити оновлення'
            $script:StatusLabel.ForeColor = Color '#ffb2a5'
            $script:DetailLabel.Text = $_.Exception.Message
            $script:Primary.Text = '▶  Запустити встановлену версію'
            $script:Primary.Enabled = Test-Path -LiteralPath $app -PathType Leaf
        }
    } else {
        try {
            Start-Process -FilePath $app -WorkingDirectory $root -ErrorAction Stop
            $script:Form.Close()
        } catch {
            $script:StatusLabel.Text = 'Не вдалося запустити програму'
            $script:DetailLabel.Text = $_.Exception.Message
            Write-LauncherLog ('Помилка запуску: ' + $_.Exception.Message)
        }
    }
})
$script:Form.Add_Shown({ Refresh-LauncherState })
$script:Form.Add_FormClosed({ if ($logo.Image) { $logo.Image.Dispose() } })
[System.Windows.Forms.Application]::Run($script:Form)
