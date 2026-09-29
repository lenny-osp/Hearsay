# Hearsay translation glossary

Shared terms for the four interface translations. Use these exact words so
the same concept is always translated the same way. When a string is not
covered here, follow the platform conventions of macOS in that language
(the wording Apple uses in Finder, System Settings, and Voice Memos).

## Never translate

Hearsay, Whisper, MLX, GitHub Copilot CLI, Claude Code CLI, Codex CLI,
Antigravity CLI, Ollama, LM Studio, Hugging Face, SRT, WAV, Markdown, JSON,
API, CLI, URL, macOS, Finder, Keychain names shown by macOS, model ids
(such as `gemini-3.8-flash-high`), keyboard shortcut symbols (⌘ ⌥ ⌃ ⇧),
placeholders (`%@`, `%lld`, `%1$@`), language autonyms (English, 繁體中文,
简体中文, Deutsch, Español), and the language picker's short labels (EN,
ZH-TW, ZH-CN, DE, ES).

## Tone

- Short, plain, and calm. Match the length of the English where possible;
  labels in tabs, buttons, and segmented pickers must stay short.
- Buttons are verbs, as in the English ("Download", "Stop").
- German: use "Sie"-free neutral imperatives as macOS does ("Aufnahme
  starten"), no "du".
- Spanish: neutral international Spanish, infinitive or imperative as macOS
  does; no voseo.
- Traditional Chinese: Taiwan usage (zh-Hant-TW), full-width punctuation
  （，。：「」）, no spaces between Chinese characters.
- Simplified Chinese: mainland usage (zh-Hans-CN), full-width punctuation.
- Keep a space between Chinese text and embedded Latin words or numbers only
  where macOS does (e.g. "下载 1.6 GB 模型").

## Core terms

| English | Deutsch | Español | 繁體中文 | 简体中文 |
|---|---|---|---|---|
| Record (tab) | Aufnahme | Grabar | 錄音 | 录音 |
| Recording (noun) | Aufnahme | Grabación | 錄音 | 录音 |
| Start / Stop | Starten / Stoppen | Iniciar / Detener | 開始 / 停止 | 开始 / 停止 |
| Pause / Resume | Pausieren / Fortsetzen | Pausar / Reanudar | 暫停 / 繼續 | 暂停 / 继续 |
| File (tab) | Datei | Archivo | 檔案 | 文件 |
| Models (tab) | Modelle | Modelos | 模型 | 模型 |
| History (tab) | Verlauf | Historial | 歷史記錄 | 历史记录 |
| Settings (tab) | Einstellungen | Ajustes | 設定 | 设置 |
| General / Window / Output / AI (sections) | Allgemein / Fenster / Ausgabe / KI | General / Ventana / Salida / IA | 一般 / 視窗 / 輸出 / AI | 通用 / 窗口 / 输出 / AI |
| Transcript | Transkript | Transcripción | 逐字稿 | 转录稿 |
| Transcribe / Transcribing… | Transkribieren / Wird transkribiert … | Transcribir / Transcribiendo… | 轉錄 / 正在轉錄… | 转录 / 正在转录… |
| Live preview | Live-Vorschau | Vista previa en directo | 即時預覽 | 实时预览 |
| Final pass | Abschließender Durchlauf | Pasada final | 完整轉錄 | 完整转录 |
| Meeting notes / Notes | Besprechungsnotizen / Notizen | Notas de la reunión / Notas | 會議筆記 / 筆記 | 会议笔记 / 笔记 |
| Generate Notes… / Regenerate Notes… | Notizen erstellen … / Notizen neu erstellen … | Generar notas… / Regenerar notas… | 產生筆記… / 重新產生筆記… | 生成笔记… / 重新生成笔记… |
| Meeting name | Besprechungsname | Nombre de la reunión | 會議名稱 | 会议名称 |
| Rename… / Rename (History) | Umbenennen … / Umbenennen | Renombrar… / Renombrar | 重新命名… / 重新命名 | 重命名… / 重命名 |
| Microphone | Mikrofon | Micrófono | 麥克風 | 麦克风 |
| System audio | Systemaudio | Audio del sistema | 系統聲音 | 系统声音 |
| Input device | Eingabegerät | Dispositivo de entrada | 輸入裝置 | 输入设备 |
| Level meter | Pegelanzeige | Medidor de nivel | 音量表 | 音量表 |
| Silence warning | Stillewarnung | Aviso de silencio | 靜音警告 | 静音警告 |
| Language (of audio) | Sprache | Idioma | 語言 | 语言 |
| Auto (language) | Automatisch | Automático | 自動 | 自动 |
| Auto mode default language (was "Preferred language" until 2026-09-29) | Standardsprache für Automatisch | Idioma predeterminado para Automático | 自動模式預設語言 | 自动模式默认语言 |
| Interface language | Sprache der Benutzeroberfläche | Idioma de la interfaz | 介面語言 | 界面语言 |
| Model | Modell | Modelo | 模型 | 模型 |
| Download / Downloading… | Laden / Wird geladen … | Descargar / Descargando… | 下載 / 正在下載… | 下载 / 正在下载… |
| Delete / Move to Trash… | Löschen / In den Papierkorb legen … | Eliminar / Trasladar a la Papelera… | 刪除 / 丟到垃圾桶… | 删除 / 移到废纸篓… |
| Reveal in Finder | Im Finder zeigen | Mostrar en el Finder | 在 Finder 中顯示 | 在访达中显示 |
| Output folder | Ausgabeordner | Carpeta de salida | 輸出資料夾 | 输出文件夹 |
| Provider | Anbieter | Proveedor | 提供者 | 提供方 |
| Token | Token | Token | 權杖 | 令牌 |
| Test connection | Verbindung testen | Probar conexión | 測試連線 | 测试连接 |
| Reasoning effort | Denkaufwand | Esfuerzo de razonamiento | 推理強度 | 推理强度 |
| Prompt template | Prompt-Vorlage | Plantilla de instrucciones | 提示詞範本 | 提示词模板 |
| Send / Keep local | Senden / Lokal behalten | Enviar / Mantener en local | 送出 / 保留在本機 | 发送 / 保留在本地 |
| Menu bar | Menüleiste | Barra de menús | 選單列 | 菜单栏 |
| Dock | Dock | Dock | Dock | 程序坞 |
| Hotkey / Shortcut | Tastaturkurzbefehl | Atajo de teclado | 快速鍵 | 快捷键 |
| Launch at login | Beim Anmelden öffnen | Abrir al iniciar sesión | 登入時開啟 | 登录时打开 |
| Acknowledgements | Danksagungen | Agradecimientos | 致謝 | 致谢 |
| Unfinished recording (crash recovery) | Unvollständige Aufnahme | Grabación sin terminar | 未完成的錄音 | 未完成的录音 |
| Permission denied | Zugriff verweigert | Permiso denegado | 權限遭拒 | 权限被拒绝 |
| Restart Now / Later | Jetzt neu starten / Später | Reiniciar ahora / Más tarde | 立即重新啟動 / 稍後 | 立即重新启动 / 稍后 |

macOS system locations keep Apple's wording in each language, for example
"System Settings > Privacy & Security > Microphone" becomes
"Systemeinstellungen > Datenschutz & Sicherheit > Mikrofon",
"Ajustes del Sistema > Privacidad y seguridad > Micrófono",
「系統設定 > 隱私權與安全性 > 麥克風」,
「系统设置 > 隐私与安全性 > 麦克风」.
