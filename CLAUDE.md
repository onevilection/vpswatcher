# CLAUDE.md

このファイルは Claude Code がセッション開始時に読み込むプロジェクト規約です。
このプロジェクトで作業する際は、以下のルールを必ず守ってください。

---

## このプロジェクトについて

- **アプリ名**: VPS監視-Mei（内部識別子・パス名は `VpsWatcher`）
- **概要**: N台のUbuntu VPSのCPU/メモリ/ストレージ/ネットワークを、Windowsデスクトップ常駐ガジェットで1Hzリアルタイム表示する監視アプリ。状態をキャラ表情で示し、しきい値超過時にVOICEPEAK音声で警告する。
- **配布**: 組織（会社）GitHubの**モノレポ**。GitHub Releases（タグ由来）からダウンロードして使う公開配布アプリ。
- **構成は2コンポーネント（言語が異なる）**:
  - **agent/** … サーバ側エージェント。**Go 静的バイナリ**（`CGO_ENABLED=0`）。`/proc`・`statfs`を直接読んでNDJSONを1行/秒でstdoutへ流すだけのステートレス。
  - **client/** … Windowsクライアント。**C# / .NET（最新LTS）/ WPF**。self-contained単一ファイルで配布。
- **このプロジェクトは PHP/Laravel ではない**。composer・artisan・vipass-auth・MySQLは一切使わない。
- 詳細仕様は `docs/design.md`（設計書）を正典とする。着手前に必ず参照すること。

---

## ディレクトリ構造（厳守）

```
vpswatcher/
├── .gitignore
├── CLAUDE.md
├── README.md             # 導入手順・SmartScreen注意・設定方法
├── CHANGELOG.md          # タグごとの変更履歴
├── LICENSE               # 本体MIT（依存 gong=BSD-3 / OxyPlot=MIT と両立）
├── servers.example.json  # 設定テンプレ（ダミー値のみ。実値は絶対に置かない）
├── .claude/
│   └── settings.json     # Claude Code 権限設定
├── agent/                # Goエージェント（VPS側で開発・テスト）
│   ├── go.mod
│   ├── main.go
│   └── internal/         # /proc・statfs読み取り、NDJSON出力など
├── client/               # WPFクライアント（Windows側で開発・テスト）
│   ├── VpsWatcher.sln
│   └── VpsWatcher/       # App, Views, ViewModels, Services, Assets
├── docs/                 # 設計書・手順（md）。秘密は書かない
│   ├── design.md         # 設計書（正典）
│   └── ndjson-schema.md  # NDJSONスキーマ契約（単一の真実）
├── testdata/
│   └── sample.ndjson     # ゴールデンサンプル（両側のテストが参照）
└── .github/
    └── workflows/
        └── release.yml   # タグ連動でGo/WPFをビルドしReleaseへ
```

### ファイル作成時のルール
- サーバ側ロジック → `agent/` 配下。外部コマンド（df/cat/ip等）を**forkせず** `/proc`・`statfs` を直接読む。
- クライアント側ロジック → `client/VpsWatcher/` の適切なサブフォルダ（MVVM: Views / ViewModels / Services）。
- 設計書・メモ・手順 → `docs/` に md。
- スキーマに関わる変更 → 必ず `docs/ndjson-schema.md` と `testdata/sample.ndjson` を同時に更新する（後述）。
- 一時的な作業ファイルはリポジトリに残さない。

---

## NDJSONスキーマは「単一の真実」（最重要・契約のドリフト防止）

agent と client は別マシンの別セッションで開発するため、**出力スキーマとパーサのズレ**が最大の事故源。

- スキーマの正典は **`docs/ndjson-schema.md` ＋ `testdata/sample.ndjson`**。
- agent 側は「sample.ndjson に準拠した出力を吐く」テストを持つ。
- client 側は「sample.ndjson を正しくパースする」テストを持つ。**両側が同じfixtureを参照**する。
- スキーマを変えるときは、契約ファイル → 両側のテスト → 実装、の順で更新する。契約を更新せずに片側だけ実装を変えない。
- スキーマ整合の確認には、サブエージェント `consistency-checker` を使う。

---

## 低負荷を最優先（基本設計原則・ハードルール）

24時間常駐するため、サーバ・ローカル双方で「アイドル時CPUほぼ0%・メモリ最小」を満たすこと。

### サーバ側（agent）
- 外部コマンドをforkしない（`/proc`・`statfs`直接read）。
- FDを使い回す（`seek(0)`再read）。バッファ再確保を避ける。
- `sleep`ベースの1Hz（ビジーループ禁止）。正常時はログを吐かない。

### クライアント側（client）
- イベント駆動・1Hz更新のみ。ポーリング/タイマー連打をしない。
- `INotifyPropertyChanged`で変化プロパティだけ通知。毎秒の全画面再描画をしない。
- イラストPNGは起動時に1回デコードして `Freeze()` キャッシュ。状態遷移時のみ差し替え。
- 音声WAVは1回ロード。常駐アニメーションを置かない。
- 履歴はタイムスタンプ非保持＋リング（直近1h）＋1分ロールアップで有界化。グラフ描画はポップアップ表示中のみ＋画面幅へ間引き。

---

## 秘密情報の扱い（公開リポジトリのため最優先）

- **実サーバのIP・SSH秘密鍵・ホスト鍵(known_host)値・実際の `servers.json` を絶対にコミットしない。** リポジトリには `servers.example.json`（ダミー値）のみ置く。
- 実行時の本物の設定はユーザー環境の `%APPDATA%\VpsWatcher\servers.json` / `state.json` に置かれる（リポジトリ外）。
- SSH秘密鍵は各マシンの `~/.ssh` にあり、リポジトリには持ち込まない。
- ログ・デバッグ出力・コミットメッセージに秘密や個人情報を出さない。
- ※私的リポジトリでは設定コミットは無害だったが、**公開では即漏洩**。発想を切り替えること。

---

## セキュリティ設計（実装時に守る）

- エージェントは**listenしない**。到達経路はSSH（既存ポート・鍵認証）のみ。公開ポートを増やさない。
- 監視鍵は authorized_keys の **forced-command** でエージェント起動のみに束縛する設計（鍵が漏れてもシェルを取らせない）。
- クライアントは接続先の**ホスト鍵をピン留め**して検証する（MITM防止）。
- セキュリティに関わる実装（SSH接続・鍵・forced-command・ホスト鍵検証）は、サブエージェント `critical-security-reviewer` のレビューを通し、自律で確定しない。

---

## git / GitHub 運用方針

- リポジトリは**組織（会社）GitHub**に手動作成済み。個人アカウントはコラボレーター。リモートは組織名のURL（例 `git@github.com:<org>/vpswatcher.git`）。
- ブランチモデルは **GitHub Flow**：`main`は常にビルド可能な統合ブランチ。作業は短命なフィーチャーブランチで行い、PR経由で`main`へ。
- **公開物はタグから作る**：`main`の良コミットに SemVer タグ（`vX.Y.Z`）→ CIがGo各アーキ＋WPF self-containedをビルドしReleaseへ（SHA256添付）。利用者はReleaseから取得。
- **mainそのものは配布物ではない**（mainからタグで切り出したReleaseが配布物）。

### コミット前の必須確認
- `git status` で、実 `servers.json`・鍵・ビルド成果物（`bin/`・`obj/`）が**追跡対象に入っていないこと**を確認してからコミットする。
- 一度コミットした秘密は履歴に残り消えない。最初のコミット前に `.gitignore` の正しさを確認する。
- 管理対象は「人が書いたもの（コード・設計書・設定テンプレート）」。生成物・秘密・ログは管理しない。

---

## 権限方針（自律でよい操作 / 承認が必要な操作）

詳細は `.claude/settings.json` に従う。基本方針は以下。

### 自律で進めてよい（確認不要）
- ファイル読み取り、コード・設計書の調査と要約
- このリポジトリ内のファイル作成・編集
- `go build` / `go test` / `go vet` / `gofmt`、`dotnet build` / `dotnet test` / `dotnet format`
- ローカルでのブランチ作成・コミット
- **`main` 以外（短命なフィーチャーブランチ）への `git push`**（`origin` の feature ブランチへ。GitHub Flow の通常作業）
- **PR の作成（`gh pr create`）**

### 必ず人間の承認を得る
- **`main` への直接 push** / PR の**マージ**（`main` への統合） / `gh release`
- 依存追加・更新（`go get` / `dotnet add package`）
- `dotnet publish`（配布物の生成）
- `ssh` / `scp` / `curl`（リモートVPSへの接続・実機結合テスト・取得）
- SSH・鍵・forced-command・ホスト鍵検証に関わる実装
- スキーマ契約（`docs/ndjson-schema.md` / `testdata/sample.ndjson`）の変更

戻せない・共有・本番・外部に影響する操作は、実行前に確認すること。

---

## 開発分業（2マシン）

- **VPS側 Claude Code**：`agent/`（Go）。`/proc`・`statfs`の実機検証ができるLinux環境で開発・テスト。
- **Windows側 Claude Code**：`client/`（WPF）。Windowsでのビルド・実行・透過ウィンドウ確認。
- 進め方：先にVPS側でエージェント＋スキーマを凍結しゴールデンサンプルを産出 → Windows側はそれに対しオフライン開発 → 最後に実SSH接続で結合テスト（本番と同じ経路）。

---

## 作業の進め方

- 大きなタスクは、まず `docs/` に方針を整理してから着手する。
- 独立したコンポーネント内の作業は自律で進めてよい。スキーマ契約・共有部分に触れる作業は先に全体方針を確認する。
- 実装後はビルド・テスト・整形を通し、結果を報告する。
- 不明点や前提が崩れたときは、勝手に補完せず確認する。
