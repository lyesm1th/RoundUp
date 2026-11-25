# Джерела графічного лаунчера GeoZamir

## Що у файлах

- `GeoZamir_Launcher.exe` — готовий x64 Windows GUI bootstrap (без консольного вікна).
- `launcher/GeoZamir_Launcher.ps1` — Windows Forms GUI, перевірка локального оновлення, встановлення після кнопки, запуск програми.
- `launcher/native_win64/launcher_stub.c` — мінімальний Windows x64 завантажувач; викликає `CreateProcessW` для запуску GUI PowerShell у фоновому режимі, використовуючи абсолютний шлях до своєї папки.
- `launcher/native_win64/kernel32_mock.c` — **тільки допоміжний файл збірки**, в реліз він не потрапляє.
- `launcher/native_win64/build_pe.py` — збирає готовий Windows EXE за допомогою GCC, objcopy і GNU ld з підтримкою `i386pep` (MinGW та Windows не потрібні). Створює PE resource icon із PNG.
- `launcher/assets` — логотип у SVG, PNG та ICO.

## Збірка

У Linux зі стандартними GCC/Binutils:

```bash
python3 launcher/native_win64/build_pe.py
```

Результат — `GeoZamir_Launcher.exe`. Поряд із ним для запуску потрібні `launcher/GeoZamir_Launcher.ps1`, `launcher/assets`, `launcher.config.json`, `current-version.json` і `GeoZamir.exe`.

**Примітка:** Python-скрипт виправляє локальні релокації після ELF→PE перетворення й перевіряє структуру, а остаточний EXE користується стандартною таблицею імпортів та ASLR. Тільки mock DLL використовується під час збірки; у реліз вона не входить. Користувачеві встановлювати компілятор не потрібно.

Підтримка віддаленого сервера не реалізована — відповідно до режиму Solo. Windows-запуск ще потрібно перевірити на реальному ПК.
