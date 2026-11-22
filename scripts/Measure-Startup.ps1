param(
    [int]$Runs = 5,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "DnsQuery\DnsQuery.csproj"
$output = Join-Path $root "DnsQuery\bin\$Configuration\net10.0"
$temporary = Join-Path $root ".startup-benchmark"
$certificate = Join-Path $temporary "benchmark.pfx"
$password = "dnsquery-local-benchmark"

New-Item -ItemType Directory -Path $temporary -Force | Out-Null
try {
    dotnet build $project -c $Configuration -p:NuGetAudit=false | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    try {
        $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
            "CN=localhost",
            $rsa,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $san = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
        $san.AddDnsName("localhost")
        $request.CertificateExtensions.Add($san.Build())
        $generatedCertificate = $request.CreateSelfSigned(
            [DateTimeOffset]::UtcNow.AddMinutes(-1),
            [DateTimeOffset]::UtcNow.AddDays(1))
        try {
            [System.IO.File]::WriteAllBytes(
                $certificate,
                $generatedCertificate.Export(
                    [System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx,
                    $password))
        } finally {
            $generatedCertificate.Dispose()
        }
    } finally {
        $rsa.Dispose()
    }

    $results = @()
    for ($run = 1; $run -le $Runs; $run++) {
        $portProbe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::IPv6Loopback, 0)
        $portProbe.Start()
        $dotPort = ([System.Net.IPEndPoint]$portProbe.LocalEndpoint).Port
        $portProbe.Stop()
        $portProbe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::IPv6Loopback, 0)
        $portProbe.Start()
        $dohPort = ([System.Net.IPEndPoint]$portProbe.LocalEndpoint).Port
        $portProbe.Stop()

        $startInfo = [System.Diagnostics.ProcessStartInfo]::new("dotnet", "DnsQuery.dll")
        $startInfo.WorkingDirectory = $output
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production"
        $startInfo.Environment["DnsProxy__BaseDnsServer"] = "127.0.0.1"
        $startInfo.Environment["DnsProxy__BaseDnsPort"] = "9"
        $startInfo.Environment["DnsProxy__CertificatePath"] = $certificate
        $startInfo.Environment["DnsProxy__CertificatePassword"] = $password
        $startInfo.Environment["DnsProxy__DotPort"] = $dotPort.ToString()
        $startInfo.Environment["DnsProxy__DohPort"] = $dohPort.ToString()

        $timer = [System.Diagnostics.Stopwatch]::StartNew()
        $process = [System.Diagnostics.Process]::Start($startInfo)
        try {
            $ready = $false
            while ($timer.Elapsed -lt [TimeSpan]::FromSeconds(15)) {
                try {
                    $client = [System.Net.Sockets.TcpClient]::new([System.Net.Sockets.AddressFamily]::InterNetworkV6)
                    $task = $client.ConnectAsync([System.Net.IPAddress]::IPv6Loopback, $dotPort)
                    if ($task.Wait(50) -and $client.Connected) {
                        $ready = $true
                        $client.Dispose()
                        break
                    }
                    $client.Dispose()
                } catch {
                }
                Start-Sleep -Milliseconds 10
            }
            $timer.Stop()
            if (-not $ready) { throw "Service did not become ready." }
            $process.Refresh()
            $results += [pscustomobject]@{
                run = $run
                readyMilliseconds = $timer.Elapsed.TotalMilliseconds
                workingSetBytes = $process.WorkingSet64
                privateMemoryBytes = $process.PrivateMemorySize64
            }
        } finally {
            if (-not $process.HasExited) {
                $process.Kill($true)
                $process.WaitForExit()
            }
            $process.Dispose()
        }
    }

    [pscustomobject]@{
        measured = $true
        framework = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        runs = $results
        averageReadyMilliseconds = ($results | Measure-Object readyMilliseconds -Average).Average
        averageWorkingSetBytes = ($results | Measure-Object workingSetBytes -Average).Average
        averagePrivateMemoryBytes = ($results | Measure-Object privateMemoryBytes -Average).Average
    } | ConvertTo-Json -Depth 4
} finally {
    if (Test-Path -LiteralPath $certificate) { Remove-Item -LiteralPath $certificate -Force }
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}
