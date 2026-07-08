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
    python personal_space.py --sensors 8      # Editor のセンサー数と合わせる

VRChat 側で OSC を有効化しておくこと (Action Menu > Options > OSC > Enabled)。
停止するときは Ctrl+C。停止時に移動入力を 0 に戻す。
"""
import argparse
import math
import threading
import time

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


def main():
    ap = argparse.ArgumentParser(description="VRChat personal-space keeper (OSC)")
    ap.add_argument("--sensors", type=int, default=8, help="センサー数(Editor と一致させる)")
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

    n = max(args.sensors, 3)
    escape_dirs = _build_escape_dirs(n)

    state = [0.0] * n
    enabled = {"v": True}   # メニュー PS_Enabled。既定 ON(パラメータ未受信でも動く)
    lock = threading.Lock()
    got_any = {"v": False}  # 一度でも受信したか(トラブルシュート用)

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

    dispatcher = Dispatcher()
    for i in range(n):
        dispatcher.map(f"/avatar/parameters/PS_{i}", make_handler(i))
    # メニュー ON/OFF (Expression Menu の PS_Enabled) を購読して全体を停止できるように
    dispatcher.map("/avatar/parameters/PS_Enabled", enabled_handler)

    use_oscquery = _OSCQUERY_AVAILABLE and not args.no_oscquery
    service = None  # 参照を保持して GC/広告停止を防ぐ

    if use_oscquery:
        listen_port = get_open_udp_port()
        http_port = get_open_tcp_port()
        server = ThreadingOSCUDPServer(("127.0.0.1", listen_port), dispatcher)

        service = OSCQueryService("PersonalSpace", http_port, listen_port)
        for i in range(n):
            try:
                service.advertise_endpoint(f"/avatar/parameters/PS_{i}", 0.0)
            except Exception:
                pass  # 広告に失敗しても、サービス発見だけで受信できることが多い

        found = _discover_vrchat_input()
        if found:
            send_ip, send_port = found
        else:
            send_ip, send_port = "127.0.0.1", 9000
            print("VRChat を OSCQuery で発見できませんでした。送信は 127.0.0.1:9000 を使用します。")
        print(f"[OSCQuery] listen={listen_port} http={http_port}  ->  send {send_ip}:{send_port}")
    else:
        if args.no_oscquery:
            reason = "--no-oscquery 指定"
        else:
            reason = "tinyoscquery 未インストール"
        listen_port = args.listen_port
        send_ip, send_port = args.send_ip, args.send_port
        server = ThreadingOSCUDPServer(("127.0.0.1", listen_port), dispatcher)
        print(f"[固定ポート:{reason}] listen {listen_port}  ->  send {send_ip}:{send_port}")

    threading.Thread(target=server.serve_forever, daemon=True).start()
    client = SimpleUDPClient(send_ip, send_port)
    print("Ctrl+C で停止します")

    period = 1.0 / max(args.rate, 1.0)
    smooth = _clamp(args.smooth, 0.0, 0.99)
    v_s, h_s = 0.0, 0.0
    ox_s, oz_s = 0.0, 0.0          # リモートオフセット(平滑後)
    send_offset = not args.no_remote_offset
    warned = False
    start = time.time()

    try:
        while True:
            with lock:
                values = list(state)
                is_enabled = enabled["v"]

            # メニューで OFF のときは移動もオフセットも止め、入力を 0 に戻す。
            if not is_enabled:
                v_s = h_s = ox_s = oz_s = 0.0
                client.send_message("/input/Vertical", 0.0)
                client.send_message("/input/Horizontal", 0.0)
                if send_offset:
                    client.send_message("/avatar/parameters/PS_OffX", 0.0)
                    client.send_message("/avatar/parameters/PS_OffZ", 0.0)
                if args.debug:
                    print("PS_Enabled=OFF  待機中           ", end="\r")
                time.sleep(period)
                continue

            # 全センサーの逃走ベクトルを近さで重み付けして合成。
            ex = ez = 0.0
            for i in range(n):
                w = values[i]
                if w <= 0.0:
                    continue
                dx, dz = escape_dirs[i]
                ex += dx * w
                ez += dz * w

            # ez=前後(前が近い→後退で負), ex=左右(右が近い→左へで負)。
            v = _clamp(ez * args.gain, -1.0, 1.0)
            h = _clamp(ex * args.gain, -1.0, 1.0)
            if args.invert_v:
                v = -v
            if args.invert_h:
                h = -h

            # 平滑化して急な移動を防ぐ
            v_s = v_s * smooth + v * (1.0 - smooth)
            h_s = h_s * smooth + h * (1.0 - smooth)

            out_v = 0.0 if abs(v_s) < args.deadzone else v_s
            out_h = 0.0 if abs(h_s) < args.deadzone else h_s

            client.send_message("/input/Vertical", float(out_v))
            client.send_message("/input/Horizontal", float(out_h))

            # リモート位置ズラし(遅延補償): 逃走方向をアバター相対のオフセットとして同期送信。
            # 入力の反転フラグとは無関係(アバターローカル空間で適用されるため raw の ex/ez を使う)。
            if send_offset:
                ox = _clamp(ex * args.lead, -1.0, 1.0)
                oz = _clamp(ez * args.lead, -1.0, 1.0)
                ox_s = ox_s * smooth + ox * (1.0 - smooth)
                oz_s = oz_s * smooth + oz * (1.0 - smooth)
                px = 0.0 if abs(ox_s) < args.deadzone else ox_s
                pz = 0.0 if abs(oz_s) < args.deadzone else oz_s
                client.send_message("/avatar/parameters/PS_OffX", float(px))
                client.send_message("/avatar/parameters/PS_OffZ", float(pz))

            # 10秒経っても1つも受信していなければヒントを出す
            if not warned and not got_any["v"] and time.time() - start > 10:
                warned = True
                print("\n[!] パラメータを受信していません。VRChat の OSC が ON か、Editor の"
                      "『パラメータを同期する』ON で再アップロードを試してください。")

            if args.debug:
                near = max(values) if values else 0.0
                print(f"near{near:.2f} escape({ex:+.2f},{ez:+.2f}) "
                      f"-> V{out_v:+.2f} H{out_h:+.2f}   ", end="\r")

            time.sleep(period)
    except KeyboardInterrupt:
        pass
    finally:
        # 移動入力を必ず 0 に戻す（戻さないと動き続けるため）
        client.send_message("/input/Vertical", 0.0)
        client.send_message("/input/Horizontal", 0.0)
        print("\nstopped. inputs reset to 0")


if __name__ == "__main__":
    main()
