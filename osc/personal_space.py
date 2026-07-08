#!/usr/bin/env python3
"""VRChat パーソナルスペース維持アプリ (OSC)。

アバターの N 個の Contact Receiver が出力する PS_0 … PS_{N-1}(放射状の近さ)を受信し、
全センサーから逃走ベクトルを合成して、侵入者の逆方向へ /input/Vertical・/input/Horizontal
を送る常駐アプリ。センサー i は角度 θ=360*i/N (i=0 が正面) に対応する。

接続方式:
  - 既定は OSCQuery(自動接続)。ポート指定は不要で、VRChat を自動発見する。
    起動しておけば VRChat 側でアバターを切り替えるだけで有効化される。
  - tinyoscquery が無い / 発見できない場合は固定ポート(受信9001, 送信9000)へ自動フォールバック。
    --no-oscquery を付けると最初から固定ポートを使う。

使い方:
    pip install -r requirements.txt
    python personal_space.py --sensors 16     # Editor のセンサー数と合わせる

    GUI(起動状態・ログをウィンドウで見たい)場合:
    python personal_space_gui.py

VRChat 側で OSC を有効化しておくこと (Action Menu > Options > OSC > Enabled)。
停止するときは Ctrl+C。停止時に移動入力を 0 に戻す。
"""
import argparse
import math
import threading
import time
from dataclasses import dataclass

from pythonosc.dispatcher import Dispatcher
from pythonosc.osc_server import ThreadingOSCUDPServer
from pythonosc.udp_client import SimpleUDPClient

try:
    from tinyoscquery.queryservice import OSCQueryService
    from tinyoscquery.query import OSCQueryBrowser, OSCQueryClient
    from tinyoscquery.utility import get_open_tcp_port, get_open_udp_port
    _OSCQUERY_AVAILABLE = True
except ImportError:
    _OSCQUERY_AVAILABLE = False


def _clamp(v, lo, hi):
    return max(lo, min(hi, v))


def _build_escape_dirs(n):
    """センサー i (角度 θ=2πi/n, i=0 が正面+Z) の「逃げる向き」= -(sinθ, cosθ)。"""
    dirs = []
    for i in range(n):
        theta = 2.0 * math.pi * i / n
        dirs.append((-math.sin(theta), -math.cos(theta)))
    return dirs


def _discover_vrchat_input(timeout=3.0):
    """OSCQuery で VRChat の OSC 入力先(ip, port)を探す。見つからなければ None。"""
    browser = OSCQueryBrowser()
    deadline = time.time() + timeout
    while time.time() < deadline:
        for svc in browser.get_discovered_oscquery():
            try:
                client = OSCQueryClient(svc)
                host = client.get_host_info()
                if host and host.name and "VRChat" in host.name:
                    return host.osc_ip, host.osc_port
            except Exception:
                continue
        time.sleep(0.25)
    return None


@dataclass
class Config:
    """CLI 引数と同じ設定項目。GUI からも同じ Config を作って渡す。"""
    sensors: int = 16
    no_oscquery: bool = False
    listen_port: int = 9001
    send_ip: str = "127.0.0.1"
    send_port: int = 9000
    rate: float = 20.0
    gain: float = 1.5
    deadzone: float = 0.05
    smooth: float = 0.4
    invert_h: bool = False
    invert_v: bool = False
    no_remote_offset: bool = False
    lead: float = 1.0
    debug: bool = False


# ワーカーの状態コード。GUI 側で色・ラベルに変換する。
STATUS_STOPPED = "stopped"      # 停止中
STATUS_STARTING = "starting"    # 接続処理中(サーバ起動・VRChat 探索)
STATUS_LISTENING = "listening"  # 起動済み・パラメータ受信待ち
STATUS_RUNNING = "running"      # パラメータ受信中(動作中)
STATUS_NO_VRCHAT = "no_vrchat"  # VRChat 未検出(フォールバック送信)
STATUS_ERROR = "error"          # 起動失敗


class PersonalSpaceWorker:
    """OSC 受信〜移動送信のループを 1 本のワーカースレッドで回す。

    CLI からも GUI からも使う。log(コールバック)にメッセージを流し、status/telemetry を
    公開する。telemetry は参照ごと差し替える(読み手はロック不要)。
    """

    def __init__(self, config: Config, log=None):
        self.cfg = config
        self._log = log if log is not None else (lambda m: print(m))
        self._thread = None
        self._stop = threading.Event()
        self._server = None
        self._service = None  # OSCQuery サービス参照を保持(GC/広告停止を防ぐ)
        self.status = STATUS_STOPPED
        self.telemetry = {
            "near": 0.0, "ex": 0.0, "ez": 0.0,
            "out_v": 0.0, "out_h": 0.0,
            "enabled": True, "received": False, "send": "",
        }

    # ---- 外部 API ----
    def log(self, msg):
        try:
            self._log(msg)
        except Exception:
            pass

    def is_running(self):
        return self._thread is not None and self._thread.is_alive()

    def start(self):
        if self.is_running():
            return
        self._stop.clear()
        self.status = STATUS_STARTING
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()

    def stop(self):
        self._stop.set()
        t = self._thread
        if t is not None:
            t.join(timeout=3.0)
        self._teardown()
        self.status = STATUS_STOPPED

    # ---- 内部 ----
    def _teardown(self):
        if self._server is not None:
            try:
                self._server.shutdown()
            except Exception:
                pass
            try:
                self._server.server_close()
            except Exception:
                pass
            self._server = None
        if self._service is not None:
            # tinyoscquery の版差を吸収して広告を止める(mDNS の残留を防ぐ)
            for name in ("stop", "close"):
                fn = getattr(self._service, name, None)
                if callable(fn):
                    try:
                        fn()
                    except Exception:
                        pass
            zc = getattr(self._service, "zeroconf", None) or \
                getattr(self._service, "_zeroconf", None)
            if zc is not None:
                try:
                    zc.close()
                except Exception:
                    pass
            self._service = None

    def _run(self):
        cfg = self.cfg
        n = max(cfg.sensors, 3)
        escape_dirs = _build_escape_dirs(n)

        state = [0.0] * n
        enabled = {"v": True}   # メニュー PS_Enabled。既定 ON(未受信でも動く)
        lock = threading.Lock()
        got_any = {"v": False}

        def make_handler(idx):
            def handler(_address, *osc_args):
                if not osc_args:
                    return
                try:
                    value = float(osc_args[0])
                except (TypeError, ValueError):
                    return
                with lock:
                    state[idx] = value
                    got_any["v"] = True
            return handler

        def enabled_handler(_address, *osc_args):
            if not osc_args:
                return
            with lock:
                enabled["v"] = bool(osc_args[0])

        tuning = {"range": None, "gain": None, "lead": None}

        def make_tuning_handler(key):
            def handler(_address, *osc_args):
                if not osc_args:
                    return
                try:
                    value = float(osc_args[0])
                except (TypeError, ValueError):
                    return
                with lock:
                    tuning[key] = value
            return handler

        dispatcher = Dispatcher()
        for i in range(n):
            dispatcher.map(f"/avatar/parameters/PS_{i}", make_handler(i))
        dispatcher.map("/avatar/parameters/PS_Enabled", enabled_handler)
        dispatcher.map("/avatar/parameters/PS_Range", make_tuning_handler("range"))
        dispatcher.map("/avatar/parameters/PS_Gain", make_tuning_handler("gain"))
        dispatcher.map("/avatar/parameters/PS_Lead", make_tuning_handler("lead"))

        use_oscquery = _OSCQUERY_AVAILABLE and not cfg.no_oscquery

        try:
            if use_oscquery:
                listen_port = get_open_udp_port()
                http_port = get_open_tcp_port()
                self._server = ThreadingOSCUDPServer(("127.0.0.1", listen_port), dispatcher)

                self._service = OSCQueryService("PersonalSpace", http_port, listen_port)
                for i in range(n):
                    try:
                        self._service.advertise_endpoint(f"/avatar/parameters/PS_{i}", 0.0)
                    except Exception:
                        pass

                self.log("VRChat を OSCQuery で探索中…")
                found = _discover_vrchat_input()
                if found:
                    send_ip, send_port = found
                    self.status = STATUS_LISTENING
                else:
                    send_ip, send_port = "127.0.0.1", 9000
                    self.status = STATUS_NO_VRCHAT
                    self.log("VRChat を発見できませんでした。送信は 127.0.0.1:9000 を使用します。")
                self.log(f"[OSCQuery] listen={listen_port} http={http_port}  ->  send {send_ip}:{send_port}")
            else:
                reason = "--no-oscquery 指定" if cfg.no_oscquery else "tinyoscquery 未インストール"
                listen_port = cfg.listen_port
                send_ip, send_port = cfg.send_ip, cfg.send_port
                self._server = ThreadingOSCUDPServer(("127.0.0.1", listen_port), dispatcher)
                self.status = STATUS_LISTENING
                self.log(f"[固定ポート:{reason}] listen {listen_port}  ->  send {send_ip}:{send_port}")
        except Exception as e:
            self.status = STATUS_ERROR
            self.log(f"[エラー] 起動に失敗しました: {e}")
            self._teardown()
            return

        threading.Thread(target=self._server.serve_forever, daemon=True).start()
        client = SimpleUDPClient(send_ip, send_port)
        self.telemetry = dict(self.telemetry, send=f"{send_ip}:{send_port}")
        self.log("動作を開始しました（停止するまで自分だけ離れます）")

        period = 1.0 / max(cfg.rate, 1.0)
        smooth = _clamp(cfg.smooth, 0.0, 0.99)
        v_s, h_s = 0.0, 0.0
        ox_s, oz_s = 0.0, 0.0
        send_offset = not cfg.no_remote_offset
        warned = False
        start = time.time()

        try:
            while not self._stop.is_set():
                with lock:
                    values = list(state)
                    is_enabled = enabled["v"]
                    received = got_any["v"]
                    t_range, t_gain, t_lead = tuning["range"], tuning["gain"], tuning["lead"]

                if received and self.status in (STATUS_LISTENING, STATUS_NO_VRCHAT):
                    self.status = STATUS_RUNNING

                rng = t_range if t_range is not None else 1.0
                cutoff = 1.0 - (0.2 + 0.8 * rng)
                gain = (3.0 * t_gain) if t_gain is not None else cfg.gain
                lead = (2.0 * t_lead) if t_lead is not None else cfg.lead

                if not is_enabled:
                    v_s = h_s = ox_s = oz_s = 0.0
                    client.send_message("/input/Vertical", 0.0)
                    client.send_message("/input/Horizontal", 0.0)
                    if send_offset:
                        client.send_message("/avatar/parameters/PS_OffX", 0.0)
                        client.send_message("/avatar/parameters/PS_OffZ", 0.0)
                    self.telemetry = dict(self.telemetry, near=0.0, ex=0.0, ez=0.0,
                                          out_v=0.0, out_h=0.0, enabled=False, received=received)
                    time.sleep(period)
                    continue

                ex = ez = 0.0
                denom = 1.0 - cutoff
                for i in range(n):
                    w = values[i]
                    if w <= cutoff or denom <= 0.0:
                        continue
                    w = (w - cutoff) / denom
                    dx, dz = escape_dirs[i]
                    ex += dx * w
                    ez += dz * w

                v = _clamp(ez * gain, -1.0, 1.0)
                h = _clamp(ex * gain, -1.0, 1.0)
                if cfg.invert_v:
                    v = -v
                if cfg.invert_h:
                    h = -h

                v_s = v_s * smooth + v * (1.0 - smooth)
                h_s = h_s * smooth + h * (1.0 - smooth)

                out_v = 0.0 if abs(v_s) < cfg.deadzone else v_s
                out_h = 0.0 if abs(h_s) < cfg.deadzone else h_s

                client.send_message("/input/Vertical", float(out_v))
                client.send_message("/input/Horizontal", float(out_h))

                if send_offset:
                    ox = _clamp(ex * lead, -1.0, 1.0)
                    oz = _clamp(ez * lead, -1.0, 1.0)
                    ox_s = ox_s * smooth + ox * (1.0 - smooth)
                    oz_s = oz_s * smooth + oz * (1.0 - smooth)
                    px = 0.0 if abs(ox_s) < cfg.deadzone else ox_s
                    pz = 0.0 if abs(oz_s) < cfg.deadzone else oz_s
                    client.send_message("/avatar/parameters/PS_OffX", float(px))
                    client.send_message("/avatar/parameters/PS_OffZ", float(pz))

                if not warned and not received and time.time() - start > 10:
                    warned = True
                    self.log("[!] パラメータを受信していません。VRChat の OSC が ON か、"
                             "Editor の『パラメータを同期する』ON で再アップロードを試してください。")

                near = max(values) if values else 0.0
                self.telemetry = dict(self.telemetry, near=near, ex=ex, ez=ez,
                                      out_v=out_v, out_h=out_h, enabled=True, received=received)
                time.sleep(period)
        finally:
            try:
                client.send_message("/input/Vertical", 0.0)
                client.send_message("/input/Horizontal", 0.0)
            except Exception:
                pass
            self.log("停止しました（移動入力を 0 に戻しました）")


def _config_from_args(args) -> Config:
    return Config(
        sensors=args.sensors, no_oscquery=args.no_oscquery, listen_port=args.listen_port,
        send_ip=args.send_ip, send_port=args.send_port, rate=args.rate, gain=args.gain,
        deadzone=args.deadzone, smooth=args.smooth, invert_h=args.invert_h,
        invert_v=args.invert_v, no_remote_offset=args.no_remote_offset, lead=args.lead,
        debug=args.debug,
    )


def main():
    ap = argparse.ArgumentParser(description="VRChat personal-space keeper (OSC)")
    ap.add_argument("--sensors", type=int, default=16, help="センサー数(Editor と一致させる)")
    ap.add_argument("--no-oscquery", action="store_true", help="OSCQuery を使わず固定ポート")
    ap.add_argument("--listen-port", type=int, default=9001, help="固定モードの受信ポート")
    ap.add_argument("--send-ip", default="127.0.0.1")
    ap.add_argument("--send-port", type=int, default=9000, help="固定モードの送信ポート")
    ap.add_argument("--rate", type=float, default=20.0, help="送信レート(Hz)")
    ap.add_argument("--gain", type=float, default=1.5, help="押し出しの強さ")
    ap.add_argument("--deadzone", type=float, default=0.05, help="この値以下の入力は 0 にする")
    ap.add_argument("--smooth", type=float, default=0.4, help="平滑化 0=即時 〜 0.99=最大")
    ap.add_argument("--invert-h", action="store_true", help="左右が逆に動くとき付ける")
    ap.add_argument("--invert-v", action="store_true", help="前後が逆に動くとき付ける")
    ap.add_argument("--no-remote-offset", action="store_true",
                    help="リモート位置ズラし(遅延補償)の同期パラメータ送信を止める")
    ap.add_argument("--lead", type=float, default=1.0,
                    help="リモート先行量の倍率(0で無効)。アバター側の最大リード距離に掛かる")
    ap.add_argument("--debug", action="store_true", help="逃走ベクトルと出力を表示する")
    args = ap.parse_args()

    worker = PersonalSpaceWorker(_config_from_args(args), log=lambda m: print(m))
    worker.start()
    print("Ctrl+C で停止します")
    try:
        while worker.is_running():
            if args.debug:
                t = worker.telemetry
                print(f"near{t['near']:.2f} escape({t['ex']:+.2f},{t['ez']:+.2f}) "
                      f"-> V{t['out_v']:+.2f} H{t['out_h']:+.2f}   ", end="\r")
            time.sleep(0.1)
    except KeyboardInterrupt:
        pass
    finally:
        worker.stop()


if __name__ == "__main__":
    main()
