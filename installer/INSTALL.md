# ACadSmartLeader — ручная установка

Пакет для AutoCAD **2020–2024 x64**. Также сохранена отдельная сборка для
AutoCAD 2025+ (.NET 8). AutoCAD LT не поддерживает этот .NET-плагин.
Установка выполняется обычным копированием папки, **без инсталлятора и прав
администратора**. Все файлы плагина размещаются в профиле текущего пользователя:

```text
%APPDATA%\Autodesk\ApplicationPlugins\ACadSmartLeader.bundle
```

## Установка

1. Закройте все экземпляры AutoCAD.
2. Скачайте `ACadSmartLeader-manual-install-v<версия>.zip` из раздела Releases.
   В свойствах ZIP нажмите «Разблокировать», если такой флажок доступен, затем
   распакуйте архив во временную папку. Не загружайте DLL прямо из ZIP.
3. Нажмите `Win+R`, введите `%APPDATA%\Autodesk\ApplicationPlugins` и откройте
   папку. Если она отсутствует, создайте её внутри `%APPDATA%\Autodesk`.
4. Скопируйте **всю папку `ACadSmartLeader.bundle`** из архива в эту папку.
   Не копируйте только DLL и не создавайте второй уровень `ACadSmartLeader.bundle`.
5. Запустите AutoCAD. Пакет обнаруживается и загружается автоматически; вручную
   выполнять `NETLOAD` или добавлять DLL в автозагрузку не требуется.
6. Для проверки введите в командной строке `SL_SETTINGS` или `SL_RULES`. Для создания выноски
   используйте `SL_CREATE` или `SL_CREATE_GROUP` (групповое правило с ID,
   оканчивающимся на `_GROUP`), для обновления — `SL_UPDATE`.

После копирования структура должна быть следующей (остальные файлы не удалять):

```text
%APPDATA%\Autodesk\ApplicationPlugins\ACadSmartLeader.bundle\
  PackageContents.xml
  THIRD-PARTY-NOTICES.txt
  Contents\
    AutoCAD2020\ACadSmartLeader.dll
    AutoCAD2021-2024\ACadSmartLeader.dll
    AutoCAD2025\ACadSmartLeader.dll
```

В каждой из трёх папок рядом с DLL находятся `label-rules.json` и
`demo-schema.json`. AutoCAD сам выбирает подходящую сборку:

| Версия AutoCAD | Сборка | Среда выполнения |
| --- | --- | --- |
| 2020 (R23.1) | `Contents\AutoCAD2020` | .NET Framework 4.7 или более новый 4.x |
| 2021–2024 (R24.0–R24.3) | `Contents\AutoCAD2021-2024` | .NET Framework 4.8 |
| 2025+ (R25.0+) | `Contents\AutoCAD2025` | .NET 8 |

Эти среды требуются самим AutoCAD; архив плагина их не устанавливает.
NCalc и его зависимости встроены в DLL. Библиотеки Autodesk поставляет AutoCAD,
они не включены в архив. Палитра acadPaletteSetXDATA не нужна для загрузки команд;
для работы выносок нужны данные BobrovXDATA и схема в чертеже.
Настройки пользователя сохраняются в `%APPDATA%\ACadSmartLeader\settings.json`.

## Если команды не появились

- Проверьте, что `PackageContents.xml` находится непосредственно в
  `%APPDATA%\Autodesk\ApplicationPlugins\ACadSmartLeader.bundle`.
- При предупреждении о неподписанной DLL разрешите загрузку, если доверяете
  источнику. При необходимости добавьте только каталог этого пакета с подпапками
  в доверенные расположения: `OPTIONS` → «Файлы» → «Надёжные папки»
  (`TRUSTEDPATHS`). Не отключайте `SECURELOAD`.
- Если `APPAUTOLOAD=0`, автоматическая загрузка отключена настройками AutoCAD.
  Используйте `APPAUTOLOADER` или восстановите разрешённую политикой автозагрузку.
- Перезапустите AutoCAD после разблокировки скачанного архива и повторного копирования.
  Не загружайте одновременно старую DLL через `NETLOAD` и новую через bundle.
- Для диагностики можно временно задать `SMARTLEADER_TRACE=1` перед запуском
  AutoCAD; по умолчанию трассировка выключена.

## Обновление и удаление

При переходе с версии 0.1.3 или более ранней выгрузите группу `ACADSMARTLEADER`
командой `CUIUNLOAD`, если она есть в профиле AutoCAD. Повторите для каждого
профиля, в котором загружалась эта группа.

Закройте AutoCAD. При обновлении сохраните резервную копию своих изменённых JSON,
удалите старую папку `ACadSmartLeader.bundle` и скопируйте новую целиком. Это
предотвращает смешивание DLL разных выпусков. Правила в уже сохранённых чертежах
и настройки `%APPDATA%\ACadSmartLeader` при замене пакета не удаляются.

Для удаления закройте AutoCAD и удалите только
`%APPDATA%\Autodesk\ApplicationPlugins\ACadSmartLeader.bundle`.
При желании отдельно удалите `%APPDATA%\ACadSmartLeader` с настройками.
Выноски в DWG остаются обычными MLeader с последним сохранённым текстом.

SHA256 архива опубликован в `release-manifest.txt` рядом с ZIP в каждом выпуске.
Проверить скачанный файл можно командой PowerShell `Get-FileHash <путь-к-ZIP> -Algorithm SHA256`.
