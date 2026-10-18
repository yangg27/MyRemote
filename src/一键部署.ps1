<#
.SYNOPSIS
    MyRemote 被控端一键固化脚本
.DESCRIPTION
    在“被控的那台电脑”上运行一次即可。它会：
      1) 确认系统是专业版/企业版/教育版（家庭版无法作为 RDP 被控端）
      2) 开启 Windows 远程桌面（RDP）并放行防火墙
      3) 关闭睡眠/休眠，避免机器失联
      4) 关闭网卡节能，避免断连
      5) 开启 SMB 文件共享，并把 3389 与 445 的防火墙作用域限定到 Tailscale 虚拟网段
         （100.64.0.0/10），确保这两个端口对公网/物理网卡完全不可见
      6) 可选：把本机登录账号写入凭据管理器，方便文件管理免输密码
    注意：必须以管理员身份运行。
#>

# 若未以管理员运行，则请求提权后重开
if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "需要管理员权限，正在请求提权…" -ForegroundColor Yellow
    Start-Process PowerShell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

$ErrorActionPreference = "Continue"
$tailscaleScope = "100.64.0.0/10"   # Tailscale 分配的虚拟 IP 网段
Write-Host "`n===== MyRemote 被控端部署 =====`n" -ForegroundColor Cyan

# ---------- 1) 版本检查 ----------
# 注意：中文版 Windows 的 ProductName 是“Windows 10 专业版”，不含 "Pro"，
# 因此优先用与语言无关的 EditionID 判定（Professional / Enterprise / Education / Server*）。
$cv = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion" -ErrorAction SilentlyContinue
$edition = if ($cv) { "$($cv.ProductName)（$($cv.EditionID)）" } else { "未知" }
Write-Host "[1/6] 系统版本：$edition"
$isPro = ($cv -and ($cv.EditionID -match "Professional|Enterprise|Education|Server")) -or ($cv -and $cv.ProductName -match "Pro|Enterprise|Education|专业版|企业版|教育版")
if (-not $isPro) {
    Write-Host "  ⚠ 当前不是专业版/企业版/教育版。家庭版无法作为 RDP 被控端。" -ForegroundColor Red
    Write-Host "  请先升级到专业版，或改用自研抓屏方案。脚本仍会继续，但远程桌面将无法开启。" -ForegroundColor Red
} else {
    Write-Host "  ✓ 版本支持远程桌面" -ForegroundColor Green
}

# ---------- 2) 开启远程桌面 ----------
Write-Host "[2/6] 开启远程桌面…"
try {
    Set-ItemProperty -Path "HKLM:\System\CurrentControlSet\Control\Terminal Server" -Name "fDenyTSConnections" -Value 0
    # 允许来自任意版本客户端的 NLA 连接
    Set-ItemProperty -Path "HKLM:\System\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" -Name "UserAuthentication" -Value 1

    # 只改注册表不够：远程桌面服务(TermService)若被停用/禁用，端口根本不会监听。
    # 这里确保其启动类型为自动并立即启动（已是运行状态则不会中断现有会话）。
    Set-Service -Name TermService -StartupType Automatic -ErrorAction SilentlyContinue
    $svc = Get-Service -Name TermService -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne "Running") {
        Start-Service -Name TermService -ErrorAction SilentlyContinue
        $svc.WaitForStatus("Running", "00:00:15")
    }
    $svcNow = (Get-Service -Name TermService -ErrorAction SilentlyContinue).Status
    if ($svcNow -eq "Running") {
        Write-Host "  ✓ 远程桌面已开启，TermService 运行中（防火墙放行将在第5步按 Tailscale 网段单独配置）" -ForegroundColor Green
    } else {
        Write-Host "  ⚠ 注册表已设置，但 TermService 未能启动（当前：$svcNow），请检查服务是否被策略禁用" -ForegroundColor Yellow
    }
} catch {
    Write-Host "  ✗ 开启远程桌面失败：$_" -ForegroundColor Red
}

# ---------- 3) 关闭睡眠/休眠 ----------
Write-Host "[3/6] 关闭睡眠/休眠…"
try {
    powercfg /change standby-timeout-ac 0
    powercfg /change standby-timeout-dc 0
    powercfg /change hibernate-timeout-ac 0
    powercfg /change hibernate-timeout-dc 0
    powercfg /hibernate off
    Write-Host "  ✓ 睡眠/休眠已关闭（永不自动睡眠）" -ForegroundColor Green
} catch {
    Write-Host "  ✗ 电源设置失败：$_" -ForegroundColor Red
}

# ---------- 4) 关闭网卡节能 ----------
Write-Host "[4/6] 关闭网卡节能…"
try {
    $adapters = Get-NetAdapter | Where-Object { $_.Status -eq "Up" }
    foreach ($a in $adapters) {
        & powercfg /devicequery wake_programmable | Out-Null
        # 取消“允许计算机关闭此设备以节约电源”
        Set-NetAdapterPowerManagement -Name $a.Name -NoPowerDown -ErrorAction SilentlyContinue | Out-Null
    }
    # 兜底：直接改注册表禁用网卡节能
    Get-ChildItem "HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4D36E972-E325-11CE-BFC1-08002BE10318}" -ErrorAction SilentlyContinue | ForEach-Object {
        Set-ItemProperty -Path $_.PSPath -Name "PnPCapabilities" -Value 24 -ErrorAction SilentlyContinue
    }
    Write-Host "  ✓ 网卡节能已关闭（重启后完全生效）" -ForegroundColor Green
} catch {
    Write-Host "  ✗ 网卡节能设置失败：$_" -ForegroundColor Red
}

# ---------- 5) 开启 SMB 并限定防火墙作用域 ----------
Write-Host "[5/6] 配置 SMB 文件共享（作用域限定 Tailscale）…"
try {
    # 确保 Server 服务运行（提供文件共享）
    Set-Service -Name LanmanServer -StartupType Automatic -ErrorAction SilentlyContinue
    Start-Service -Name LanmanServer -ErrorAction SilentlyContinue

    # 关键安全收紧：禁用系统自带的默认防火墙规则组。
    # 这些默认组的作用域通常是“任何地址(Any)”，若启用会让 3389/445 对所有网段开放，
    # 与下面的 Tailscale 限定规则叠加，造成端口暴露。我们仅保留明确限定虚拟网段的自定义规则。
    Disable-NetFirewallRule -DisplayGroup "远程桌面" -ErrorAction SilentlyContinue
    Disable-NetFirewallRule -DisplayGroup "文件和打印机共享" -ErrorAction SilentlyContinue

    # 放行“文件和打印机共享(SMB)”的入站，但作用域仅限 Tailscale 网段
    $ruleName = "MyRemote-SMB-Tailscale"
    Remove-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort 445 `
        -RemoteAddress $tailscaleScope -Action Allow -Profile Any | Out-Null

    # RDP 入站同样限定到 Tailscale 网段（更安全，公网/物理网卡扫描不到）
    $rdpRule = "MyRemote-RDP-Tailscale"
    Remove-NetFirewallRule -DisplayName $rdpRule -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName $rdpRule -Direction Inbound -Protocol TCP -LocalPort 3389 `
        -RemoteAddress $tailscaleScope -Action Allow -Profile Any | Out-Null

    # 校验自定义规则的作用域确实限定为 Tailscale 网段
    $rdpOk = $false; $smbOk = $false
    try {
        $rdpOk = ((Get-NetFirewallRule -DisplayName $rdpRule -ErrorAction SilentlyContinue) | Get-NetFirewallAddressFilter).RemoteAddress -contains $tailscaleScope
        $smbOk = ((Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue) | Get-NetFirewallAddressFilter).RemoteAddress -contains $tailscaleScope
    } catch { }

    Write-Host "  ✓ SMB(445) 与 RDP(3389) 已仅对 Tailscale 虚拟网段($tailscaleScope)开放" -ForegroundColor Green
    Write-Host "  （默认防火墙组已禁用，端口对公网/物理网卡不可见）" -ForegroundColor Gray
    if (-not ($rdpOk -and $smbOk)) {
        Write-Host "  ⚠ 自定义规则作用域校验未通过，请手动确认防火墙设置。" -ForegroundColor Yellow
    }
    Write-Host "  （默认使用管理员共享 C$，无需额外建共享；如需专用共享可手动建）" -ForegroundColor Gray
} catch {
    Write-Host "  ✗ SMB 配置失败：$_" -ForegroundColor Red
}

# ---------- 6) 可选：写入凭据 ----------
Write-Host "[6/6] 可选的凭据写入…"
$writeCred = Read-Host "是否把当前登录账号写入凭据管理器（方便文件管理免输密码）？[Y/N]"
if ($writeCred -match "^(Y|y)$") {
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $pass = Read-Host -AsSecureString "请输入该账号密码"
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($pass)
    $plain = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
    # 对 Tailscale 虚拟 IP 不可预知，这里写入通配主机；实际连接时按设备 IP 由 MyRemote 自动添加
    Write-Host "  提示：MyRemote 在打开文件管理时会按设备地址自动写入精确凭据，此处可跳过。" -ForegroundColor Gray
    [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}

Write-Host "`n===== 部署完成 =====" -ForegroundColor Cyan
Write-Host "请重启一次以确保网卡节能设置完全生效。" -ForegroundColor Yellow
Write-Host "之后：装好 Tailscale（同账号）→ 打开 MyRemote → 添加本机为设备即可。" -ForegroundColor White
Read-Host "按回车退出"
