#!/usr/bin/env python3
"""VRChat パーソナルスペース維持アプリ の GUI ランチャー。

起動しているか(接続ランプ)・ログ・現在の動き(逃走ベクトル/移動量)をウィンドウで確認できる。
中身の OSC 処理は personal_space.py の PersonalSpaceWorker を使う(CLI と同じロジック)。

    python personal_space_gui.py

Windows では PersonalSpace.bat をダブルクリックしても起動できる(コンソール無し)。
Tkinter は Python 標準添付なので追加インストールは不要。
"""
import argparse
import os
import queue
import socket
import subprocess
import sys
import tkinter as tk
from tkinter import ttk
from tkinter.scrolledtext import ScrolledText

from personal_space import (
    Config, PersonalSpaceWorker,
    STATUS_STOPPED, STATUS_STARTING, STATUS_LISTENING,
    STATUS_RUNNING, STATUS_NO_VRCHAT, STATUS_ERROR,
)
from personal_space_watch import GUI_PORT

# 状態コード -> (表示ラベル, ランプ色)
STATUS_VIEW = {
    STATUS_STOPPED:   ("停止中", "#888888"),
    STATUS_STARTING:  ("接続中…", "#e0a020"),
    STATUS_LISTENING: ("待機中（VRChat受信待ち）", "#2a7fff"),
    STATUS_RUNNING:   ("動作中", "#22c060"),
    STATUS_NO_VRCHAT: ("VRChat未検出（フォールバック送信）", "#e0a020"),
    STATUS_ERROR:     ("エラー", "#e04040"),
}


# ---- Windows スタートアップ登録（VRChat 連動の常駐ウォッチャを起動時に立てる）----
def _startup_dir():
    return os.path.join(os.environ.get("APPDATA", ""),
                        "Microsoft", "Windows", "Start Menu", "Programs", "Startup")


def _startup_bat_path():
    return os.path.join(_startup_dir(), "PersonalSpaceWatch.bat")


def is_autostart_registered():
    return os.path.isfile(_startup_bat_path())


def register_autostart():
    osc_dir = os.path.dirname(os.path.abspath(__file__))
    content = ('@echo off\r\n'
               'cd /d "%s"\r\n'
               'start "" pythonw personal_space_watch.py\r\n' % osc_dir)
    os.makedirs(_startup_dir(), exist_ok=True)
    with open(_startup_bat_path(), "w", encoding="utf-8") as f:
        f.write(content)


def unregister_autostart():
    p = _startup_bat_path()
    if os.path.isfile(p):
        os.remove(p)


def _running_from_package():
    """このスクリプトが Unity パッケージ内(Packages/com.vrc-personal-space)から動いているか。

    その場所から自動起動を登録すると、プロジェクトを消した時にパスが壊れるため警告に使う。
    """
    here = os.path.abspath(__file__).replace("\\", "/").lower()
    return "packages/com.vrc-personal-space" in here


def _launch_watcher():
    """今すぐ常駐ウォッチャを起動する（ウォッチャ側で単一インスタンス保証）。"""
    if os.name != "nt":
        return
    osc_dir = os.path.dirname(os.path.abspath(__file__))
    try:
        subprocess.Popen(["pythonw", "personal_space_watch.py"], cwd=osc_dir,
                         creationflags=0x08000000)
    except Exception:
        pass


def acquire_single_instance():
    """GUI 起動検知用ポートを握る。既に使われていれば None（別 GUI が起動中）。"""
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        s.bind(("127.0.0.1", GUI_PORT))
        s.listen(1)
        return s
    except OSError:
        try:
            s.close()
        except Exception:
            pass
        return None


class App:
    def __init__(self, root, autostart=False):
        self.root = root
        root.title("VRChat Personal Space")
        root.geometry("560x520")
        root.minsize(480, 420)

        self.log_queue = queue.Queue()
        self.worker = None

        pad = {"padx": 8, "pady": 4}

        # --- 状態ランプ + 開始/停止 ---
        top = ttk.Frame(root)
        top.pack(fill="x", **pad)
        self.lamp = tk.Canvas(top, width=20, height=20, highlightthickness=0)
        self.lamp_dot = self.lamp.create_oval(3, 3, 17, 17, fill="#888888", outline="")
        self.lamp.pack(side="left", padx=(2, 8))
        self.status_label = ttk.Label(top, text="停止中", font=("", 13, "bold"))
        self.status_label.pack(side="left")

        self.start_btn = ttk.Button(top, text="開始", command=self.on_start)
        self.start_btn.pack(side="right")
        self.stop_btn = ttk.Button(top, text="停止", command=self.on_stop, state="disabled")
        self.stop_btn.pack(side="right", padx=(0, 6))

        # --- 設定 ---
        cfgf = ttk.LabelFrame(root, text="設定")
        cfgf.pack(fill="x", **pad)
        self.v_sensors = tk.IntVar(value=16)
        self.v_gain = tk.DoubleVar(value=1.5)
        self.v_smooth = tk.DoubleVar(value=0.4)
        self.v_no_oscquery = tk.BooleanVar(value=False)
        self.v_no_offset = tk.BooleanVar(value=False)
        self.v_inv_h = tk.BooleanVar(value=False)
        self.v_inv_v = tk.BooleanVar(value=False)

        row1 = ttk.Frame(cfgf)
        row1.pack(fill="x", padx=6, pady=3)
        ttk.Label(row1, text="センサー数").pack(side="left")
        ttk.Spinbox(row1, from_=4, to=16, width=5, textvariable=self.v_sensors).pack(side="left", padx=(4, 14))
        ttk.Label(row1, text="押し出しの強さ").pack(side="left")
        ttk.Spinbox(row1, from_=0.1, to=5.0, increment=0.1, width=6, textvariable=self.v_gain).pack(side="left", padx=(4, 14))
        ttk.Label(row1, text="平滑化").pack(side="left")
        ttk.Spinbox(row1, from_=0.0, to=0.95, increment=0.05, width=6, textvariable=self.v_smooth).pack(side="left", padx=4)

        row2 = ttk.Frame(cfgf)
        row2.pack(fill="x", padx=6, pady=3)
        ttk.Checkbutton(row2, text="固定ポート(OSCQuery不使用)", variable=self.v_no_oscquery).pack(side="left")
        ttk.Checkbutton(row2, text="遅延補償を送らない", variable=self.v_no_offset).pack(side="left", padx=(12, 0))

        row3 = ttk.Frame(cfgf)
        row3.pack(fill="x", padx=6, pady=3)
        ttk.Checkbutton(row3, text="左右反転", variable=self.v_inv_h).pack(side="left")
        ttk.Checkbutton(row3, text="前後反転", variable=self.v_inv_v).pack(side="left", padx=(12, 0))

        # --- 自動起動（VRChat 連動）---
        autof = ttk.LabelFrame(root, text="自動起動")
        autof.pack(fill="x", **pad)
        self.v_autostart = tk.BooleanVar(value=is_autostart_registered())
        ttk.Checkbutton(
            autof, text="Windows起動時に自動で常駐（VRChat 起動に合わせてこのアプリを立ち上げる）",
            variable=self.v_autostart, command=self.on_toggle_autostart).pack(
            side="left", padx=6, pady=4)

        # --- ライブ表示 ---
        self.live_label = ttk.Label(root, text="—", font=("Consolas", 10))
        self.live_label.pack(fill="x", **pad)

        # --- ログ ---
        logf = ttk.LabelFrame(root, text="ログ")
        logf.pack(fill="both", expand=True, **pad)
        self.log = ScrolledText(logf, height=10, state="disabled", wrap="word", font=("Consolas", 9))
        self.log.pack(fill="both", expand=True, padx=4, pady=4)

        root.protocol("WM_DELETE_WINDOW", self.on_close)
        self._append_log("『開始』を押すと OSC を起動します。VRChat 側で OSC を ON にしておいてください。")
        if autostart:
            self._append_log("VRChat 連動で自動起動しました。")
            self.on_start()
        self._poll()

    def on_toggle_autostart(self):
        try:
            if self.v_autostart.get():
                if _running_from_package():
                    self._append_log(
                        "[注意] このアプリを Unity パッケージ内から起動しています。"
                        "その場所を自動起動に登録するとプロジェクト削除でパスが壊れます。"
                        "Editor の『OSCアプリをPCにインストール』でコピーした固定フォルダの "
                        "PersonalSpace.bat から起動して登録するのを推奨します。")
                register_autostart()
                _launch_watcher()  # 再ログインを待たず今すぐ常駐させる
                self._append_log("自動起動を登録しました（次回以降 Windows 起動時に常駐。今回分も起動済み）。")
            else:
                unregister_autostart()
                self._append_log("自動起動の登録を解除しました"
                                 "（今動いている常駐は次回 Windows 再起動で止まります）。")
        except Exception as e:
            self._append_log(f"[エラー] 自動起動の設定に失敗: {e}")
            self.v_autostart.set(is_autostart_registered())

    # ---- ボタン ----
    def on_start(self):
        if self.worker is not None and self.worker.is_running():
            return
        cfg = Config(
            sensors=int(self.v_sensors.get()),
            gain=float(self.v_gain.get()),
            smooth=float(self.v_smooth.get()),
            no_oscquery=bool(self.v_no_oscquery.get()),
            no_remote_offset=bool(self.v_no_offset.get()),
            invert_h=bool(self.v_inv_h.get()),
            invert_v=bool(self.v_inv_v.get()),
        )
        self.worker = PersonalSpaceWorker(cfg, log=self.log_queue.put)
        self.worker.start()
        self.start_btn.config(state="disabled")
        self.stop_btn.config(state="normal")
        self._set_settings_state("disabled")

    def on_stop(self):
        if self.worker is not None:
            self.stop_btn.config(state="disabled")
            self._append_log("停止しています…")
            self.worker.stop()
        self.start_btn.config(state="normal")
        self._set_settings_state("normal")

    def on_close(self):
        try:
            if self.worker is not None and self.worker.is_running():
                self.worker.stop()
        finally:
            self.root.destroy()

    # ---- 表示更新 ----
    def _poll(self):
        # ログを吐き出す
        while True:
            try:
                msg = self.log_queue.get_nowait()
            except queue.Empty:
                break
            self._append_log(msg)

        # ステータスランプ・ライブ値
        if self.worker is not None:
            status = self.worker.status
            label, color = STATUS_VIEW.get(status, ("—", "#888888"))
            self.status_label.config(text=label)
            self.lamp.itemconfig(self.lamp_dot, fill=color)
            t = self.worker.telemetry
            en = "ON" if t.get("enabled", True) else "OFF(メニュー)"
            self.live_label.config(
                text=(f"near {t.get('near', 0):.2f}  "
                      f"escape ({t.get('ex', 0):+.2f}, {t.get('ez', 0):+.2f})  "
                      f"-> V {t.get('out_v', 0):+.2f}  H {t.get('out_h', 0):+.2f}   "
                      f"[{en}]  send {t.get('send', '') or '—'}"))
            # ワーカーが自然終了していたらボタンを戻す
            if not self.worker.is_running() and self.stop_btn["state"] == "normal":
                self.start_btn.config(state="normal")
                self.stop_btn.config(state="disabled")
                self._set_settings_state("normal")
        self.root.after(150, self._poll)

    def _append_log(self, msg):
        self.log.config(state="normal")
        self.log.insert("end", str(msg).rstrip() + "\n")
        self.log.see("end")
        self.log.config(state="disabled")

    def _set_settings_state(self, state):
        # 動作中は設定を触れないようにする(センサー数変更などはワーカー再起動が必要なため)
        for frame in self.root.winfo_children():
            if isinstance(frame, ttk.LabelFrame) and frame.cget("text") == "設定":
                for row in frame.winfo_children():
                    for w in row.winfo_children():
                        try:
                            w.config(state=state)
                        except tk.TclError:
                            pass


def main():
    ap = argparse.ArgumentParser(description="VRChat Personal Space GUI")
    ap.add_argument("--autostart", action="store_true",
                    help="起動と同時に OSC を開始する（ウォッチャからの自動起動用）")
    args = ap.parse_args()

    # 二重起動防止（既に GUI が動いていれば何もしない）
    lock = acquire_single_instance()
    if lock is None:
        return

    root = tk.Tk()
    app = App(root, autostart=args.autostart)
    app._lock = lock  # ライフタイム保持（GC で閉じないように）
    root.mainloop()


if __name__ == "__main__":
    main()
