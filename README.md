# DROPME для Windows

DROPME связывает Android-устройство и Windows-компьютер в локальной Wi-Fi сети.

## Backend файлового доступа

Порядок работы:

1. WinFsp 2.2.26215 — основной backend виртуального диска Windows.
2. Windows WebClient/WebDAV — резервный режим.
3. Браузерный интерфейс — последний fallback.

WinFsp создаёт диск в Проводнике Windows и передаёт файловые операции Android-хранилищу.

В главном окне доступна настройка **«Использовать веб-интерфейс вместо WinFsp»**. В этом режиме DROPME не создаёт виртуальный диск и открывает Android-хранилище напрямую в браузере. Настройка сохраняется между запусками.

## Требования

- Windows 10/11 x64.
- WinFsp 2.2.26215.
- .NET 8 Desktop Runtime x64 (для framework-dependent запуска) или self-contained публикация DROPME.

Установщик WinFsp:

`Files/winfsp-2.2.26215.msi`

## Сборка

Только x64:

```powershell
dotnet build .\DROPME.csproj -c Debug -p:Platform=x64
```

```powershell
dotnet publish .\DROPME.csproj -c Release -p:Platform=x64
```

Конфигурация сборки x64 задана в `DROPME.csproj`. Release остаётся single-file self-contained приложением; содержимое bundle извлекается при запуске, поскольку официальный `winfsp.net` 2.2.26215 определяет свою версию через физический путь `winfsp-msil.dll`.

## Каталоги

- `Sources/utils/WinFspDriveHost.cs` — WinFsp filesystem backend.
- `Sources/utils/WebDavDriveMapper.cs` — WebClient fallback.
- `Sources/utils/AndroidFsHttpClient.cs` — Android filesystem API.
- `Files` — дополнительные компоненты.
