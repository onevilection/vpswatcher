# Claude Code 初回指示ドラフト — VPS監視-Mei（第1セッション：VPS側 / リポジトリ初期化＋スキーマ凍結）

> 使い方：この内容を、**dev VPS上のClaude Code**に最初の指示として渡す。
> 前提：GitHubの組織リポジトリは会社アカウントで**手動作成済み**、個人アカウントはコラボレーター。
> リモートURLは会社org名のもの（例 `git@github.com:<org>/vpswatcher.git`）。

---

これから「VPS監視-Mei」の開発を始めます。あなたは agent/（Goエージェント）側を担当します。
このリポジトリは `docs/design.md`（設計書）と `CLAUDE.md`（規約）を正典とします。まず両方を読んでから着手してください。特に「NDJSONスキーマは単一の真実」「低負荷を最優先」「秘密情報の扱い（公開リポジトリ）」の節を厳守すること。

今回のゴールは **コードを書くことではなく、リポジトリの土台とスキーマ契約を凍結する**ことです。以下を順に行ってください。各ステップ後に状態を報告し、push・remote接続・依存追加など承認が必要な操作は実行前に確認すること。

## 1. 既存テンプレートの整理（確認してから実施）
このフォルダにはPHP/Laravel用テンプレートの残骸がある可能性があります。本プロジェクトはGo＋WPFなので不要です。以下を**削除候補としてリストアップし、削除してよいか確認**してください（勝手に消さない）:
`app/ config/ routes/ database/ public/ vendor/ node_modules/ storage/ tmp/ composer.json composer.lock .env .env.example`
（`docs/` は残す。設計書 `docs/design.md` が既に置かれている。旧ルートの `settings.json`・`README.md` は内容を確認のうえ、本プロジェクト版へ置換または削除を提案する）

## 2. ディレクトリ構造の作成
CLAUDE.md の構造に従って、不足しているディレクトリ・プレースホルダを作成:
```
agent/            client/（空でよい・Windows側が使う）
docs/             testdata/
.claude/          .github/workflows/
assets/           （キャラ・音声の置き場。中身は後日）
```
（`.claude/settings.json`・`CLAUDE.md`・`.gitignore`・`servers.example.json`・`docs/design.md` は既に配置済み。整合を確認すること）

## 3. ルートのファイルを配置（不足分）
- `README.md`（導入手順の骨子：エージェントのcurl取得＋SHA256検証、クライアントのダウンロードとSmartScreen注意、設定方法。中身は追って充実）
- `CHANGELOG.md`（`## [Unreleased]` だけの雛形）
- `LICENSE`（MIT。著作権者は会社名で確認）

## 4. スキーマ契約の凍結（最重要）
- `docs/ndjson-schema.md` を作成し、設計書 §3.5 のNDJSONスキーマを正典として記述（フィールド・型・null許容の意味・`v`バージョンの方針）。
- `testdata/sample.ndjson` に、スキーマ準拠の**ゴールデンサンプル1行**を置く（設計書 §3.5 の例。実IPは含めずダミーidで）。
- この2ファイルが agent/client 双方のテストの参照点になることをコメントで明記。

## 5. Goエージェントの最小スケルトン（ビルドが通るだけ）
- `agent/go.mod` を初期化（モジュール名は `github.com/<org>/vpswatcher/agent` で確認）。
- `agent/main.go` は、まずは「1秒ごとにsample準拠のダミーJSONを1行stdoutへ出すループ」だけ。実際の `/proc` 読み取りは次セッション。
- `go build ./...` と `go vet ./...` が通ることを確認。
- 低負荷規約（forkしない・sleepベース・正常時ログなし）をコメントで宣言。

## 6. Git 初期化とリモート接続（push直前で停止）
```
git init
git branch -M main
git add -A
git commit -m "chore: scaffold monorepo (agent skeleton, schema contract, docs)"
```
- ここで `git status` を見せ、**servers.json・鍵・ビルド成果物が追跡されていないこと**を確認・報告。
- `git remote add origin <組織リポジトリのURL>` は、正しいURLを私に確認してから実行。
- **push はしない**（承認操作）。`main`直pushを避け、`feat/scaffold` 等のブランチを切ってPRにする方針なら、その旨を提案してから。

## 完了報告
- 削除したもの／作成したものの一覧
- `docs/ndjson-schema.md` と `testdata/sample.ndjson` の内容
- `git status` の結果（秘密・生成物が含まれないことの確認）
- 次セッション（/proc読み取り実装、Windows側のクライアント着手）への申し送り

---

### 補足：第2セッション（Windows側）の入口（参考）
Windows側のClaude Codeは、このリポジトリを `git clone` し、`client/` で WPF プロジェクトを作成、`testdata/sample.ndjson` に対するパーサのテストから始める。実エージェントへの接続（ssh）は結合フェーズまで行わない。
