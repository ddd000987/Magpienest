# Magpienest 鵲藏

[English](README.md) | 繁體中文

Magpienest（鵲藏）是 Windows 桌面上的收藏庫，用來整理你電腦裡的插畫、漫畫、影片與遊戲。所有功能都是插件，可以在「設定」裡隨時開啟或關閉，立即生效。

## 目前狀態

早期開發中。插件核心、主視窗、設定頁、即時切換語言（English / 繁體中文）與淺色／深色主題已可使用；媒體相關功能尚未完成。

## 規劃中的功能

- **插畫**：以資料夾收藏圖片，縮圖牆、標籤、評分、我的最愛、搜尋與快速檢視器。
- **漫畫**：資料夾與 zip/rar/7z 壓縮檔視為一本書；雙頁並排、由右至左閱讀、記住讀到哪裡。
- **影片**：可播放大部分格式，接續播放，可選擇音軌與字幕。
- **遊戲**：指定 exe 或掃描整個遊戲資料夾來新增；啟動遊戲並記錄遊玩時間。
- 永遠不會移動、改名或修改你的檔案：收藏庫只是一份索引。
- 提供 Windows 10/11 x64 免安裝 zip 版。

## 建置與執行

需求：Windows 10/11 x64 與 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```
dotnet build Magpienest.slnx -c Debug
dotnet test  Magpienest.slnx -c Debug
artifacts\bin\Debug\Magpienest.App.exe
```

常用參數：`--data-dir <資料夾>` 把所有資料放在指定資料夾；`--smoke-test` 啟動、檢查所有插件後結束；`--screenshot <資料夾>` 為每個頁面存一張 PNG 後結束。

## 插件架構

一個很小的核心從 `plugins/<id>/` 資料夾載入插件，每個插件都有一份 `plugin.json` 說明檔。插件之間只透過 `Magpienest.Sdk` 裡的服務介面溝通，插件註冊的所有東西在關閉時都會自動撤銷。主視窗、設定與語言功能本身也是插件。

## 授權

[MIT](LICENSE)。第三方元件列於 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 致謝

設計參考了兩個 MIT 授權的專案：[NeeView](https://github.com/neelabo/NeeView)（圖片與書本檢視）與 [Playnite](https://github.com/JosefNemec/Playnite)（遊戲庫管理）。
