# AgentPet セットアップ手順

## 1. 前提

AgentPet は Windows WPF アプリです。

GitHub Actions の配布artifactを使う場合、.NET Runtime の別途インストールは不要です。

ソースからビルドする場合は .NET 8 SDK が必要です。

- .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0

## 2. ビルドして起動する

PowerShell でリポジトリのルートを開きます。

```powershell
dotnet restore AgentPet.sln

# 標準例: Koharu アイコン
dotnet publish AgentPet.csproj -c Release -r win-x64 --self-contained true -p:AgentPetIcon=favicon-koharu.ico -o .\publish\AgentPet-Koharu
.\publish\AgentPet-Koharu\AgentPet.exe

# 別例: Luna アイコン
# 同じ作業ツリーから連続して別アイコンで publish する場合は clean を挟みます。
dotnet clean AgentPet.sln -c Release
dotnet publish AgentPet.csproj -c Release -r win-x64 --self-contained true -p:AgentPetIcon=favicon-luna.ico -o .\publish\AgentPet-Luna
.\publish\AgentPet-Luna\AgentPet.exe
```

初回起動時、`publish\AgentPet-Koharu` または `publish\AgentPet-Luna` の `pet_data` に設定ファイルと pet データが作成されます。

終了後に再起動する場合は、配置したフォルダの `AgentPet.exe` をもう一度実行します。タスクトレイから `終了` した場合も同じです。

## 3. スタートアップ登録

Windows 起動時に自動起動したい場合は、設定画面から登録します。

1. タスクトレイの AgentPet アイコンを右クリックして `設定` を開きます。
2. `接続` タブを開きます。
3. `Windows起動時にこの AgentPet を起動する` を有効にします。
4. 表示が `登録済み` になったことを確認します。

この登録は現在のユーザーの `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` に保存されます。管理者権限は不要です。

複数プロファイルを別フォルダで運用する場合、それぞれのフォルダごとに個別登録できます。

## 4. Codex 監視として設定する

1. タスクトレイの AgentPet アイコンを右クリックして `設定` を開きます。
2. `接続` タブを開きます。
3. `状況を読む対象` を `Codex` にします。
4. `ダブルクリックで開くアプリ` を `Codex` にします。
5. 設定画面のバッジが `Codex監視` になっていることを確認します。

Codex の状況は `%USERPROFILE%\.codex\sessions\**\rollout-*.jsonl` から読みます。

## 5. Claude 監視として設定する

1. タスクトレイの AgentPet アイコンを右クリックして `設定` を開きます。
2. `接続` タブを開きます。
3. `状況を読む対象` を `Claude` にします。
4. `ダブルクリックで開くアプリ` を `VSCode` にします。
5. `Claude ホーム` を通常は `C:\Users\<user>\.claude` にします。
6. `VSCode ワークスペース` を Claude Code を開くフォルダにします。
7. 設定画面のバッジが `Claude監視` になっていることを確認します。

Claude の状況は `.claude\projects\**\*.jsonl` から読みます。利用量は `~/.claude/agentpet-rate-limits.json` があれば Claude Code statusline 由来の値を優先し、無い場合はローカル履歴から推定します。


### Claude の5時間/週間リングを公式表示に近づける

Claude Code の statusline が `rate_limits` を受け取れる環境では、`tools\claude-statusline-agentpet.py` を statusline として設定すると、AgentPet が `~/.claude/agentpet-rate-limits.json` から5時間枠と週間枠の使用率を読めます。

既存の statusline を使っている場合は、同じ内容を既存スクリプトへ組み込んでください。このファイルが無い場合、AgentPet は `.claude\projects\**\*.jsonl` からの推定値を表示します。
## 6. 複数プロファイルを同時に使う

同じ exe を2つ起動するだけだと同じ `pet_data` を共有します。別設定で複数起動したい場合は、フォルダを2つに分けてください。

例:

```text
AgentPet-Codex\AgentPet.exe
AgentPet-Claude\AgentPet.exe
```

それぞれのフォルダで一度起動し、設定画面から接続先プロファイルとpetの見た目を個別に設定します。

## 7. pet の切替と追加

`設定` -> `ペット` で Koharu / Luna を選択できます。

pet を追加する場合は、`ペット` タブの `petパッケージを追加` から ZIP ファイルを選択します。

ZIP は直下に `pet.json` と `spritesheet.webp` または `spritesheet.png` を入れてください。

```text
my-pet.zip
├─ pet.json
└─ spritesheet.webp
```

`pet.json` の最小例です。

```json
{
  "id": "my-pet",
  "displayName": "My Pet",
  "description": "設定画面に表示する説明文です。",
  "spritesheetPath": "spritesheet.webp"
}
```

`id` は1〜64文字の半角英数字で始め、半角英数字、ハイフン、アンダースコアだけを使用してください。同じ `id` の pet は、ZIP全体の安全性と画像名を検証して一時フォルダへ展開した後に置き換わります。ZIP内のサブフォルダ、未知のファイル、過大な画像、過剰な圧縮率は拒否されます。画像は1辺8192px以下かつ総画素数3,355万画素以下にしてください。

任意で `preview-idle.png` を同梱できます。これはトレイアイコンや一覧表示用の見た目を確認しやすくするための画像です。

削除機能は設定画面には出していません。不要な pet を消したい場合は、アプリを終了してから `pet_data\pets` 配下のフォルダを手動で削除してください。

## 8. API プロキシ

`接続` タブの `API プロキシ` は、OpenAI 互換 API の token 使用量を proxy 経由で記録したい場合だけ使います。

通常の Codex / Claude 状況監視では必須ではありません。

既定の転送先は OpenAI のみです。

上流APIのTLS証明書は標準検証します。自己署名証明書や検証できない証明書の上流には接続しません。

このproxyはOpenAI互換のHTTP/1.1 JSON API向けです。リクエストの `Transfer-Encoding`、HTTP pipelining、4MBを超える本文は安全のため拒否します。汎用HTTP proxyとしては使用できません。

```text
http://127.0.0.1:11435/oai
```

## 9. トラブルシュート

### 設定画面のサイズが初回だけ違う

最新版では表示直後に実ウィンドウサイズを固定しています。古い exe が起動していないか、タスクトレイから終了して起動し直してください。

### 終了後に起動方法が分からない

AgentPet を配置したフォルダの `AgentPet.exe` を実行してください。自動起動したい場合は、設定画面のスタートアップ登録を有効にしてください。

### pet が背面に隠れる

タスクトレイの AgentPet アイコンをダブルクリックすると pet を再表示できます。

### Claude の利用量がすぐ反映されない

Claude CodeのOAuth利用状況APIを1分間隔で確認します。起動直後は取得まで数秒かかることがあります。API取得に失敗した場合はstatusline由来の `~/.claude/agentpet-rate-limits.json`、その次にJSONL推定へ切り替わります。推定表示には `5h~` / `W~` が付きます。

### Codex / VSCode が開かない

設定の `ダブルクリックで開くアプリ` と、Codex / VSCode のインストール状態を確認してください。

## 10. プライバシーとログ

AgentPetは独自テレメトリを送信しません。Codex/Claudeの状況要約はローカルJSONLの直近メッセージを短縮して表示し、外部LLMへ要約を依頼しません。Claude監視時は利用率取得のため、Claude CodeのOAuth認証でAnthropicの利用状況APIへ読み取り専用リクエストを送ります。

- 読み取り: `%USERPROFILE%/.codex/sessions/**/rollout-*.jsonl`、Claudeホーム内の `projects/**/*.jsonl`、`.credentials.json` のClaude Code OAuthアクセストークン、`agentpet-rate-limits.json`。アクセストークンはAnthropic API認証だけに使い、保存・ログ出力しません
- 保存: 実行フォルダ内の `pet_data/pet_config.json`、`token_history.json`、`proxy_targets.json`、petデータ
- ログ: `pet_data/agentpet.log`。proxyデバッグを有効にした場合だけ `pet_data/debug.log`
- proxy: `127.0.0.1`だけで待ち受け、APIキーと本文は保存しません

ログを共有する場合は、ユーザー名を含むローカルパスや会話由来の情報がないか確認してください。
