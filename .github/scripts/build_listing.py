"""
GitHub Releases を走査して VPM 用の docs/index.json を更新するスクリプト。

TARGET_VERSION 環境変数が指定されている場合、そのバージョンの zip が
GitHub API に反映されるまで最大 MAX_RETRIES 回リトライする。
失敗時はエラーメッセージと手動再実行の案内を出力して終了コード 1 で落ちる。
"""
import hashlib
import json
import os
import time
import urllib.request

REPO           = "iori9973/vrc-personal-space"
PACKAGE_NAME   = "com.vrc-personal-space"
ZIP_PREFIX     = "vrc-personal-space"
TOKEN          = os.environ.get("GH_TOKEN", "")
TARGET_VERSION = os.environ.get("TARGET_VERSION", "")  # 例: "0.1.0"
MAX_RETRIES    = 6
RETRY_DELAY    = 15  # seconds


def gh_request(url: str):
    req = urllib.request.Request(url)
    req.add_header("Accept", "application/vnd.github+json")
    if TOKEN:
        req.add_header("Authorization", f"Bearer {TOKEN}")
    with urllib.request.urlopen(req) as res:
        return json.loads(res.read())


def sha256_of_url(url: str) -> str:
    req = urllib.request.Request(url)
    if TOKEN:
        req.add_header("Authorization", f"Bearer {TOKEN}")
    with urllib.request.urlopen(req) as res:
        return hashlib.sha256(res.read()).hexdigest()


def fetch_releases_with_retry() -> list:
    """
    リリース一覧を取得する。
    TARGET_VERSION が指定されている場合、対象バージョンの zip が
    API に現れるまでリトライする。
    """
    target_tag = f"v{TARGET_VERSION}" if TARGET_VERSION else None

    for attempt in range(1, MAX_RETRIES + 1):
        releases = gh_request(f"https://api.github.com/repos/{REPO}/releases")

        if not target_tag:
            return releases

        target = next((r for r in releases if r["tag_name"] == target_tag), None)
        if not target:
            print(f"[{attempt}/{MAX_RETRIES}] Release {target_tag} not found in API, "
                  f"retrying in {RETRY_DELAY}s...")
            time.sleep(RETRY_DELAY)
            continue

        has_zip = any(a["name"].endswith(".zip") for a in target.get("assets", []))
        if not has_zip:
            print(f"[{attempt}/{MAX_RETRIES}] Zip asset for {target_tag} not ready yet, "
                  f"retrying in {RETRY_DELAY}s...")
            time.sleep(RETRY_DELAY)
            continue

        print(f"[{attempt}/{MAX_RETRIES}] {target_tag} zip confirmed.")
        return releases

    raise RuntimeError(
        f"\n"
        f"ERROR: v{TARGET_VERSION} の zip が {MAX_RETRIES} 回試行後も API に現れませんでした。\n"
        f"\n"
        f"以下のワークフローを手動で再実行してください:\n"
        f"  https://github.com/{REPO}/actions/workflows/release.yml\n"
    )


def main():
    releases = fetch_releases_with_retry()

    with open("package.json", encoding="utf-8") as f:
        base_pkg = json.load(f)

    with open("docs/index.json", encoding="utf-8") as f:
        index = json.load(f)

    versions = {}
    for release in releases:
        if release.get("draft") or release.get("prerelease"):
            continue

        tag     = release["tag_name"]
        version = tag.lstrip("v")
        zip_url = next(
            (a["browser_download_url"] for a in release.get("assets", [])
             if a["name"] == f"{ZIP_PREFIX}-{version}.zip"),
            None,
        )
        if not zip_url:
            print(f"[skip] {tag}: zip not found")
            continue

        print(f"[{version}] computing SHA256...")
        sha256 = sha256_of_url(zip_url)

        entry = dict(base_pkg)
        entry["version"]   = version
        entry["url"]       = zip_url
        entry["zipSHA256"] = sha256
        versions[version]  = entry

    if TARGET_VERSION and TARGET_VERSION not in versions:
        raise RuntimeError(
            f"\n"
            f"ERROR: v{TARGET_VERSION} が listing に含まれませんでした。\n"
            f"\n"
            f"以下のワークフローを手動で再実行してください:\n"
            f"  https://github.com/{REPO}/actions/workflows/release.yml\n"
        )

    index["packages"][PACKAGE_NAME]["versions"] = versions

    with open("docs/index.json", "w", encoding="utf-8") as f:
        json.dump(index, f, indent=2, ensure_ascii=False)
        f.write("\n")

    print(f"OK  docs/index.json を更新しました")
    print(f"OK  バージョン一覧: {', '.join(f'v{v}' for v in sorted(versions.keys()))}")


if __name__ == "__main__":
    main()
