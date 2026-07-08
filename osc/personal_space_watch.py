#!/usr/bin/env python3
"""VRChat の起動を監視して、GUI が未起動なら自動で立ち上げる常駐ウォッチャ（ウィンドウ無し）。

- Windows のスタートアップに登録して使う（GUI の「Windows起動時に自動起動」トグルが登録する）。
- VRChat.exe を数秒ごとに確認し、起動していて かつ GUI がまだ動いていなければ
  personal_space_gui.py を --autostart 付きで起動する（GUI は起動と同時に OSC を開始）。
- GUI の起動有無は GUI が握る単一インスタンス用ポート(GUI_PORT)への接続可否で判定する。
- このウォッチャ自身も単一インスタンス（二重常駐しない）。

手動起動する場合:  pythonw personal_space_watch.py
"""
import os
import socket
import subprocess
import sys
import time

GUI_PORT = 52017      # GUI が握る単一インスタンス用ポート（起動検知に使う）
WATCH_PORT = 52018    # このウォッチャの単一インスタンス用ポート
POLL_SEC = 5.0
CREATE_NO_WINDOW = 0x08000000 if os.name == "nt" else 0


def _single_instance(port):
    """ポートを bind できれば自分が唯一。できなければ None（既に別インスタンスあり）。"""
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        s.bind(("127.0.0.1", port))
        s.listen(1)
        return s
    except OSError:
        try:
            s.close()
        except Exception:
            pass
        return None


def _port_open(port):
    """127.0.0.1:port に接続できる（= GUI が起動中）か。"""
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as c:
        c.settimeout(0.3)
        return c.connect_ex(("127.0.0.1", port)) == 0


def _vrchat_running():
    if os.name != "nt":
        return False
    try:
        out = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq VRChat.exe", "/NH"],
            capture_output=True, text=True, creationflags=CREATE_NO_WINDOW).stdout
        return "VRChat.exe" in out
    except Exception:
        return False


def _launch_gui():
    osc_dir = os.path.dirname(os.path.abspath(__file__))
    exe = "pythonw" if os.name == "nt" else sys.executable
    try:
        subprocess.Popen([exe, "personal_space_gui.py", "--autostart"],
                         cwd=osc_dir, creationflags=CREATE_NO_WINDOW)
    except Exception:
        pass


def main():
    lock = _single_instance(WATCH_PORT)
    if lock is None:
        return  # 既にウォッチャが常駐している
    while True:
        try:
            if _vrchat_running() and not _port_open(GUI_PORT):
                _launch_gui()
                time.sleep(10.0)  # 起動直後に GUI がポートを握るまで待って二重起動を防ぐ
        except Exception:
            pass
        time.sleep(POLL_SEC)


if __name__ == "__main__":
    main()
