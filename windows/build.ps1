# Builds CardImporter.exe with the C# compiler that ships with Windows (no SDK needed).
$here = $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /win32icon:"$here\icon.ico" /out:"$here\CardImporter.exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:Microsoft.CSharp.dll "$here\CardImporter.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built: $here\CardImporter.exe"
