@echo off
REM Tüm testleri çalıştırır.
REM
REM "dotnet test" yerine test yürütülebilirleri doğrudan çağrılır: bu makinede
REM Smart App Control açık ve Microsoft imzalı testhost.exe içine yüklenen
REM imzasız test derlemelerini engelliyor (0x800711C7). xUnit v3 test projeleri
REM kendi süreçleri olarak çalıştığı için bu engele takılmıyorlar.
setlocal
cd /d "%~dp0"

dotnet build --nologo -v q || exit /b 1

echo.
echo === Cekirdek testleri ===
"tests\Takvim.Core.Tests\bin\Debug\net10.0\TakvimCekirdekTestleri.exe" || exit /b 1

echo.
echo === Veri testleri ===
"tests\Takvim.Data.Tests\bin\Debug\net10.0\TakvimVeriTestleri.exe" || exit /b 1
