# VPS監視-Mei

N台の Ubuntu VPS の CPU / メモリ / ストレージ / ネットワークを、Windows デスクトップに常駐するガジェットで **1Hz リアルタイム表示**する監視アプリ。状態をキャラクターの表情で示し、しきい値超過時に VOICEPEAK 音声で警告する。

- 監視される側に負担をかけない **低負荷最優先**設計（アイドル時 CPU ほぼ 0%）。
- 公開ポートを増やさず、既存 **SSH（鍵認証・forced-command）**のみで到達。
- 監視対象は設定追加だけで増やせ（N台可変）、パネルはドラッグで並べ替え可能。

> 詳細仕様は [`docs/design.md`](docs/design.md)（設計書・正典）を参照。

---

## 構成（2コンポーネント）

| ディレクトリ | 内容 | 技術 | 配布形態 |
|---|---|---|---|
| [`agent/`](agent/) | サーバ側エージェント。`/proc`・`statfs` を直接読み、NDJSON を 1行/秒で stdout へ流すステートレスなストリーム。 | **Go 静的バイナリ**（`CGO_ENABLED=0`） | GitHub Releases（amd64 / arm64） |
| [`client/`](client/) | Windows クライアント。SSH exec で各 VPS の stdout を受け、数値・バー・キャラ表情・音声で表示。 | **C# / .NET LTS / WPF** | GitHub Releases（self-contained 単一 exe） |

両者が交換するデータ形式は [`docs/ndjson-schema.md`](docs/ndjson-schema.md) ＋ [`testdata/sample.ndjson`](testdata/sample.ndjson) を**単一の真実**として固定する。

---

## 導入（概要・追って充実）

> ⚠️ 本セクションは骨子。実バイナリ公開（GitHub Releases）後に手順を確定する。

### サーバ側エージェント（Go）

GitHub Releases から取得し、SHA256 で改ざん検証してから配置する:

まず自分の VPS のアーキテクチャを確認する（`uname -m` の結果が `x86_64` なら amd64、`aarch64` なら arm64）。以下は **amd64** の例。arm64 の場合は `agent-linux-amd64` を `agent-linux-arm64` に読み替える（`.sha256` も同様）。

```sh
# 配置先ディレクトリを用意
sudo mkdir -p /opt/vpswatcher

cd /tmp
# バイナリと SHA256 を「元のファイル名のまま」両方ダウンロード
curl -fsSLO https://github.com/onevilection/vps-monitor-mei/releases/latest/download/agent-linux-amd64
curl -fsSLO https://github.com/onevilection/vps-monitor-mei/releases/latest/download/agent-linux-amd64.sha256

# 改ざん検証（.sha256 内のファイル名と一致するので検証が通る）
sha256sum -c agent-linux-amd64.sha256

# 検証が OK になってから配置（実行権限付与も同時）
sudo install -m 755 agent-linux-amd64 /opt/vpswatcher/agent
```

arm64（aarch64）の場合は、上記の `agent-linux-amd64` を `agent-linux-arm64` に読み替えて同じ手順を実行する。

#### 起動モデル（重要）

エージェントは**常駐サービスではない**。`cron` も `systemd` ユニットも不要で、自分でデーモン化もしない。

- クライアントが **SSH で接続したときだけ起動**し、1Hz で NDJSON を stdout へ流す。
- **SSH 切断（セッション終了）で自動的に終了**する。プロセスを残さない。
- **listen しない**：開くポートは増えない。到達経路は既存の SSH のみ。

#### 監視専用ユーザと鍵の設定

監視専用の低権限ユーザを作り、その `authorized_keys` に **forced-command** で監視鍵を束縛する（鍵が漏れてもシェルを取らせない）。`/proc`・`statfs` は world-readable なので **sudo 不要・root 不要**で全項目を取得できる。

```sh
# 監視専用ユーザを作成（forced-command 実行のためログインシェルは bash）
sudo useradd --create-home --shell /bin/bash metrics
```

> ⚠️ シェルは `/bin/bash`（`/usr/sbin/nologin` にしないこと）。forced-command はログインシェルを経由して実行されるため、nologin だとシェルが先に接続を蹴り、agent まで到達せず `This account is currently not available.` で失敗する。対話シェルや任意コマンドの実行は forced-command の `no-pty` 等が封じるので、シェルが bash でも安全性は保たれる。

クライアント側で用意した監視用**公開鍵**（次節「Windows クライアント」で生成）を、`metrics` ユーザの `~/.ssh/authorized_keys` に forced-command 付きで 1 行で登録する。

```sh
# metrics ユーザの .ssh を用意（初回のみ・権限が重要）
sudo mkdir -p /home/metrics/.ssh
sudo chmod 700 /home/metrics/.ssh

# forced-command 付きで公開鍵を 1 行追記する
# ↓ 「ssh-ed25519 AAAA... watcher-client」の部分を、watcher_ed25519.pub の中身そのものに置き換える
echo 'command="/opt/vpswatcher/agent --id=vps-example-1",no-pty,no-port-forwarding,no-X11-forwarding,no-user-rc ssh-ed25519 AAAA... watcher-client' \
  | sudo tee -a /home/metrics/.ssh/authorized_keys

# 権限と所有者を SSH の要求どおりに整える
sudo chmod 600 /home/metrics/.ssh/authorized_keys
sudo chown -R metrics:metrics /home/metrics/.ssh
```

> ⚠️ `sudo echo '...' >> ファイル` は使わない。`sudo` はリダイレクト（`>>`）に効かず、`metrics` 所有のファイルへの書き込みが Permission denied になる。上記のように `sudo tee -a` を使う。
> ⚠️ SSH は `~/.ssh`=700・`authorized_keys`=600・所有者=`metrics` でないと鍵を拒否する。上記の `chmod`/`chown` は必須。

- `--iface`/`--mounts` は**省略可**。省略時はデフォルトルートの NIC を自動判定し、ディスクは `/` を対象とする。複数マウントや IF 固定が必要なときだけ明示する。
- forced-command の各オプションの意味・フラグ詳細・セキュリティ設計は設計書 [§3.6](docs/design.md) / [§4](docs/design.md) を参照。

### Windows クライアント（WPF）

#### 監視用 SSH 鍵の準備

監視専用の SSH 鍵ペアを作る（無人で自動接続するためパスフレーズ無し）。PowerShell で:

```powershell
ssh-keygen -t ed25519 -f "$env:USERPROFILE\.ssh\watcher_ed25519" -N '""' -C "watcher-client"
```

生成された**公開鍵** `watcher_ed25519.pub` の中身を、各 VPS の `metrics` ユーザの forced-command 付き `authorized_keys` に登録する（前節「監視専用ユーザと鍵の設定」）。**秘密鍵** `watcher_ed25519` はクライアント端末から持ち出さない。

> ⚠️ **パスフレーズ無し鍵のリスク**: パスフレーズを付けていないため、秘密鍵ファイルが漏れると監視接続を再現される。forced-command で「エージェント起動のみ」に束縛しているのでシェルや任意コマンドは取られないが、秘密鍵ファイル自体の保護（`$env:USERPROFILE\.ssh` のアクセス権・端末の管理）は利用者の責任。より厳密にするならパスフレーズ＋ssh-agent を検討する。

#### 接続確認（agent の起動テスト）

アプリ導入前に、SSH で agent が起動することを手動確認する。PowerShell で（`<host>` は VPS のホスト名/IP、`<port>` は SSH ポート）:

```powershell
ssh -i "$env:USERPROFILE\.ssh\watcher_ed25519" -p <port> metrics@<host>
```

- `<port>` は各自の SSH ポートに置き換える（**デフォルトは 22**。変更している場合はその番号）。
- 初回接続時は**ホスト鍵の確認**を求められる。表示されたフィンガープリントが正しいことを確認してから受け入れる（このホスト鍵はクライアントが後でピン留め検証に使う。設計書 §4）。
- 成功すると NDJSON が 1 行/秒で流れ出す。**1 行目はレート系（`cpu_pct`/`rx_bps`/`tx_bps`）が `null`（測定中）**、2 行目以降が実値になる。`Ctrl+C` で停止する。

#### クライアントアプリ（WPF）の導入

> （WPF クライアント実装後に提供予定。以下は予定手順）

GitHub Releases から self-contained 単一 exe をダウンロードして実行する。

- ⚠️ **SmartScreen 警告**: 署名なし exe のため初回起動時に保護警告が出る。自分用／少人数なら「詳細情報 → 実行」で続行可。
- ⚠️ **サイズ**: .NET ランタイム同梱のため 100MB 超になる（ゼロインストールとのトレードオフ）。
- 監視対象は `%APPDATA%\VpsWatcher\servers.json` に定義する（テンプレートは [`servers.example.json`](servers.example.json)）。`host`/`port`/`user`(=`metrics`)/`keyPath`(=`watcher_ed25519`)/`knownHostKey`(ピン留めするホスト鍵)/`iface`/`mounts`/`thresholds` を設定する。詳細は下の「設定」節と設計書 §9.1。

---

## 設定

- サーバ定義: `%APPDATA%\VpsWatcher\servers.json`（テンプレートは [`servers.example.json`](servers.example.json)）。
  - `thresholds` は**任意**。未設定（または一部メトリクス欠落・不正値）なら設計書 §6.2 のデフォルト（cpu `[70,85,95]` / mem `[75,88,95]` / disk `[80,90,95]` / swap `[25,50,80]`）が自動適用される。明示すればサーバ単位で上書き可。
- アプリ設定・UI状態（最前面ON/OFF・並べ替え順・ウィンドウ位置）: `%APPDATA%\VpsWatcher\state.json`。
- 外観・音声（キャラ表情マップ・背景不透明度・音声マップ・音量）: `%APPDATA%\VpsWatcher\appearance.json`（テンプレートは [`appearance.example.json`](appearance.example.json)）。`background_opacity` が 0.3 未満のときは文字色を自動で濃色に切り替える。`sounds`/`master_volume`/`click` でアラート音声・音量・クリック時の音を設定（未設定なら同梱デフォルト）。表情PNG・音声WAVは `%APPDATA%\VpsWatcher\assets\char` / `…\assets\voice` に同名ファイルを置けば差し替え可。キャラをクリックすると現在の状態を読み上げる。
- **実サーバの IP・SSH 鍵・ホスト鍵・実 `servers.json` はリポジトリにコミットしない**（公開リポジトリ）。リポジトリにはダミー値の `servers.example.json` のみ置く。

---

## 開発

- ランタイムで分業: `agent/` は Linux（dev VPS）で、`client/` は Windows で開発・テスト（設計書 §15）。
- ブランチモデルは **GitHub Flow**。`main` は常にビルド可能な統合ブランチ。公開物は `main` の良コミットに付けた SemVer タグ（`vX.Y.Z`）から CI が生成する Release。
- スキーマ契約に触れる変更は `docs/ndjson-schema.md` と `testdata/sample.ndjson` を**同時更新**し、両側テストを通すこと。

---

## ライセンス

本体 MIT（予定）。依存 `gong-wpf-dragdrop`（BSD-3-Clause）・`OxyPlot`（MIT）と両立。詳細は [`LICENSE`](LICENSE)。
