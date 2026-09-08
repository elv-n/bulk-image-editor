@echo off
echo ===================================================
echo Membangun Executable (.exe) Bulk Image Resizer...
echo ===================================================

dotnet publish ImageResizerCSharp\ImageResizerCSharp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ./publish

if %ERRORLEVEL% equ 0 (
    echo.
    echo ===================================================
    echo Berhasil! File .exe tersimpan di folder:
    echo %~dp0publish\ImageResizerCSharp.exe
    echo ===================================================
) else (
    echo.
    echo Gagal membangun .exe. Silakan periksa pesan error di atas.
)
pause
