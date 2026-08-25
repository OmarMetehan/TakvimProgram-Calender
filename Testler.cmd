@echo off
REM Tüm testleri çalıştırır.
REM
REM "dotnet test" yerine test yürütülebilirleri doğrudan çağrılır: bu makinede
REM Smart App Control açık ve Microsoft imzalı testhost.exe içine yüklenen
REM imzasız test derlemelerini engelliyor (0x800711C7). xUnit v3 test projeleri
REM kendi süreçleri olarak çalıştığı için bu engele takılmıyorlar.
REM
REM Aynı denetim imzasız yürütülebilirleri karma değerine göre de
REM engelleyebiliyor ve hangi karmayı engelleyeceği önceden bilinmiyor.
REM Directory.Build.targets her hata ayıklama derlemesine yeni bir karma
REM verdiği için engel kalıcıya dönmüyor: engellenen bir derlemede yeniden
REM derleyip yeniden denenir. Kalıcı çözüm ayarın kapatılmasıdır
REM (Windows Güvenliği > Uygulama ve tarayıcı denetimi > Akıllı Uygulama
REM Denetimi), ki bu geri alınamayan bir seçimdir ve kullanıcıya aittir.
setlocal
cd /d "%~dp0"

set CEKIRDEK=tests\Takvim.Core.Tests\bin\Debug\net10.0\TakvimCekirdekTestleri.exe
set VERI=tests\Takvim.Data.Tests\bin\Debug\net10.0\TakvimVeriTestleri.exe
set GUNLUK=%TEMP%\takvim-testler.log

set DENEME=0
set HATA=0

dotnet build --nologo -v q || exit /b 1

REM Parantezli bloklar yerine etiketli döngü: cmd, blok içindeki %ERRORLEVEL%
REM değerini blok çalışmadan önce yerine koyar ve koşullar hep yanlış çıkar.
:dene
set /a DENEME+=1
set HATA=0

call :takim "Cekirdek testleri" "%CEKIRDEK%"
if errorlevel 2 goto :engellendi

call :takim "Veri testleri" "%VERI%"
if errorlevel 2 goto :engellendi

exit /b %HATA%

:engellendi
if %DENEME% GEQ 15 (
    echo.
    echo Smart App Control 15 denemede de engelledi. Ayrintilar Testler.cmd basinda.
    exit /b 1
)

echo.
echo [Smart App Control engeli] Yeniden derlenip denenecek... (%DENEME%/15)
dotnet build --nologo -v q --no-incremental >nul || exit /b 1
goto :dene

REM --------------------------------------------------------------------
REM Bir test takımını çalıştırır.
REM   0 : çalıştı (başarısız sınama olsa bile; HATA onu taşır)
REM   2 : Smart App Control engelledi, yeniden derlemeye değer
REM --------------------------------------------------------------------
:takim
echo.
echo === %~1 ===

"%~2" > "%GUNLUK%" 2>&1
set SONUC=%ERRORLEVEL%

type "%GUNLUK%"

REM Engel iki biçimde görünür: yürütülebilir hiç başlamazsa cmd'nin tek
REM satırlık uyarısı, derlemelerden biri engellenirse çalışma zamanının
REM 0x800711C7 hatası.
findstr /c:"was blocked by" /c:"0x800711C7" "%GUNLUK%" >nul
if not errorlevel 1 exit /b 2

if not "%SONUC%"=="0" set HATA=1
exit /b 0
