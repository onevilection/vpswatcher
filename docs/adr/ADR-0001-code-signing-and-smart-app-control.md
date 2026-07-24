# ADR-0001: コード署名を当面見送り、スマートアプリコントロールによるブロックを文書化で対応する

- Status: Accepted
- Date: 2026-07-24

## Context

v0.1.0 として self-contained single-file 形式で配布している `VpsWatcher.App.exe` が、開発機（Windows 11）で **Windows セキュリティによりブロックされる**事象が頻発した。

- 通知内容: 「このアプリの一部がブロックされています / アプリが読み込もうとした `VpsWatcher.Core.dll` を発行したユーザーを確認できないため、一部の機能が動作しない可能性があります」
- 実害は「一部機能の不全」にとどまらず、**アプリが起動しない**（タスクマネージャーにプロセスが現れない）。OS を再起動するまで手動実行も不可という致命的な状態。

### 根本原因

**スマートアプリコントロール（Smart App Control, 以下 SAC）**による未署名バイナリのブロック。SAC は Windows 11 22H2 で追加された機能で、署名がない、または署名が無効なバイナリを「信頼できない」と判断してブロックする。

本アプリは以下の構成のため、特に SAC に検知されやすい:

- コード署名を行っていない（未署名）
- `PublishSingleFile=true` ＋ `IncludeNativeLibrariesForSelfExtract=true` により、起動時に DLL 群を一時フォルダへ**自己展開**してから読み込む。展開先が毎回変わり、展開された DLL は素性の不明な新規ファイルとして扱われる（`VpsWatcher.Core.dll` が名指しでブロックされたのはこのため）

### SAC が有効な環境は少数派である

SAC は以下の条件を満たす環境でのみ強制モードになる:

- Windows 11 22H2 以降
- 従来はクリーンインストールが必要（Windows 10 からのアップグレード環境ではオフ）
- 対象地域が限定されている（北米・欧州から順次拡大）
- **評価モード**で約1週間デバイスの使用状況を観察し、開発者・企業ユーザーなど「SAC が邪魔になる」使い方と判定された場合は Microsoft が自動的にオフにする
- 診断データの送信がオフだと有効化できない
- 企業管理下・開発者モード設定済みの環境ではオフ

したがって大多数のユーザーは SmartScreen の警告（「詳細情報」→「実行」で通過可能）止まりであり、ハードブロックされるのは一部環境に限られる。世の未署名フリーソフトが成立しているのはこのため。

### 2026年4月の仕様変更

KB5083769（2026年4月 Patch Tuesday）以降、初期セットアップ条件を満たす環境では、管理者権限で SAC のオン/オフを切り替えられるようになった。従来は一度オフにすると Windows の再インストールが必要な一方通行だったが、この制約は解消されつつある（段階展開）。

## Decision

**コード署名は当面行わない。** 以下で対応する:

1. **README に注意書きを記載する**（SmartScreen 警告の通し方、SAC 有効時はブロックされうること、その場合のオフ手順）
2. **開発機では SAC をオフにする**。開発者は未署名バイナリを日常的にビルド・実行するため、SAC は本来オフが自然な状態（Microsoft 自身が評価モードで開発者を除外する設計思想）
3. 本 ADR に経緯と再検討条件を記録する

## Alternatives considered

### A. Azure Artifact Signing（旧 Trusted Signing）— 見送り

- Microsoft のフルマネージドコード署名サービス。証明書・秘密鍵は HSM 管理、Microsoft 管理 CA が発行
- Basic $9.99/月（5,000署名/月、超過 $0.005/署名）、Premium $99.99/月（100,000署名/月）
- **アプリ数に上限はない**。課金は署名回数（ファイル数）に対して。将来 onevilection が出す Windows 配布物すべてを同一アカウントで署名できる
- SmartScreen・SAC に対して利用直後から有効（EV 証明書と同等の即効性）。従来 EV 証明書は法人限定・年10万円前後だったことを考えると格段に安い
- 証明書は72時間の短命で自動更新されるが、署名時のタイムスタンプにより署名済みバイナリは以後も有効
- GitHub Actions 連携あり（`release.yml` に組み込み可能）
- **懸念**: 提供地域が限定されている可能性（米国・カナダ中心という報告あり）、組織向け本人確認に設立年数の要件がある（3年未満の組織は個人開発者としての申請になる可能性）。独自ドメインのメール（`info@onevilection.com`）は必須条件を満たす
- **見送り理由**: SAC 有効環境が少数派であり、実害の頻度に対してコストが見合わない段階と判断

### B. SignPath Foundation（OSS 向け無償コード署名）— 将来の有力候補

- OSS プロジェクトに無償でコード署名証明書を提供。証明書は Sectigo が発行、検証は個人の身元ではなく**リポジトリに対して**行われる。秘密鍵は HSM 管理
- 本リポジトリは MIT ライセンス・public・v0.1.0 リリース済み・README に機能説明あり・活発にメンテナンス、と主要要件を満たす見込み
- **懸念1**: 証明書は SignPath Foundation に発行されるため、**発行元（publisher）表示が SignPath Foundation になる**。「One Vilection Co., Ltd.」名義で署名したい場合は不適
- **懸念2**: 「独自の非オープンソース component を含まないこと」という条件に対し、同梱アセット（AI 生成のキャラ立ち絵、VOICEPEAK 生成音声 WAV）のライセンスが MIT でカバーされているか要整理。申請前に確認が必要
- **見送り理由**: 上記の整理コストが、現時点の実害に見合わないため。ただしコストゼロなので優先的な再検討候補

### C. single-file 発行をやめる（フォルダ配置）— 未検証

- `PublishSingleFile=false` にして self-contained のフォルダ配置にすれば、DLL の自己展開が消え固定パスに配置されるため、SAC の検知が緩和される可能性がある
- 未署名である限り根本解決ではない
- 配布物が exe 単体から zip 1つに変わる
- **未検証**。コストゼロで試せるため、実害が増えた場合の第一候補

### D. macOS 対応 — 対象外

- Windows の証明書は macOS では使用できない。macOS は Apple 独自の CA に対して署名を検証するため、サードパーティ証明書では Gatekeeper が警告・ブロックする
- 必要なもの: Apple Developer Program（年 $99）、Developer ID 証明書、**公証（notarization）**（macOS 10.15 以降は署名＋公証の両方が必須）、公証には Xcode が必要なため **Mac 実機が必要**
- 公証自体に追加料金はなく会員費に含まれる
- 本アプリは Windows 専用（WPF）のため現時点では対象外

## Consequences

### 受け入れるリスク

- SAC が有効な環境のユーザーは、本アプリを起動できない。README の注意書きで SAC をオフにする手順を案内するにとどまる
- SmartScreen の警告は全ユーザーに表示される可能性がある（クリックで通過可能）
- 「発行元不明」の表示により、配布物としての信頼性は署名済みアプリに劣る

### 再検討する条件（トリガー）

以下のいずれかに該当したら、本 ADR を見直す:

1. **ソース非公開の Windows 配布物を出すとき**（例: ov-dashboard の Windows クライアント）。OSS ではないため SignPath Foundation の対象外となり、Azure Artifact Signing が唯一の選択肢になる
2. SAC / SmartScreen によるブロックの報告が増えたとき
3. 「One Vilection Co., Ltd.」名義での署名がブランド上必要になったとき
4. 同梱アセットのライセンス整理が完了し、SignPath Foundation への申請コストが下がったとき

### 署名を導入する場合の実装上の注意

- **exe だけ署名しても解決しない**。today ブロックされたのは `VpsWatcher.Core.dll` であり、single-file は内部アセンブリを展開して読み込むため、(1) 各アセンブリを署名 → (2) `dotnet publish` で single-file 化 → (3) 生成された exe も署名、という順序が必要

## References

- Smart App Control FAQ (Microsoft Support)
- Smart App Control overview (Microsoft Learn / windows-dev-docs)
- Azure Artifact Signing (旧 Trusted Signing) 製品ページ・料金
- SignPath Foundation 利用条件（signpath.org/terms.html）
- KB5083769（2026年4月 Patch Tuesday, SAC のトグル対応）
