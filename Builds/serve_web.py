"""Serve Soccer desktop/mobile Web builds on one LAN address. Ctrl+C stops the server."""
import argparse
import functools
import gzip
import shutil
import http.server
import ipaddress
from pathlib import Path
import socket
from urllib.parse import parse_qsl, urlencode, urlsplit

ROOT = Path(__file__).resolve().parent
BUILDS = {"desktop": ROOT / "Soccer-Desktop", "mobile": ROOT / "Soccer-Mobile"}

class Handler(http.server.SimpleHTTPRequestHandler):
    extensions_map = {**http.server.SimpleHTTPRequestHandler.extensions_map, ".wasm": "application/wasm", ".data": "application/octet-stream", ".js": "application/javascript"}
    def guess_type(self, path):
        for suffix in (".gz", ".br"):
            if str(path).endswith(suffix): path = str(path)[:-len(suffix)]; break
        return super().guess_type(path)
    def do_GET(self):
        request = urlsplit(self.path)
        parts = request.path.strip("/").split("/", 1)
        kind = parts[0]
        if request.path in ("/", "/index.html"):
            agent = self.headers.get("User-Agent", "").lower()
            kind = "mobile" if any(s in agent for s in ("android", "iphone", "ipad", "mobile")) else "desktop"
            self.redirect(kind, request.query); return
        if kind not in BUILDS:
            self.send_error(404); return
        if request.path == "/" + kind:
            self.redirect(kind, request.query); return
        if not (BUILDS[kind] / "index.html").is_file():
            self.send_error(503, kind + " build is not ready"); return
        query = dict(parse_qsl(request.query, keep_blank_values=True))
        if request.path in ("/" + kind + "/", "/" + kind + "/index.html") and not query.get("host"):
            self.redirect(kind, request.query); return
        self.directory = str(BUILDS[kind])
        self.path = "/" + (parts[1] if len(parts)>1 else "") + (("?" + request.query) if request.query else "")
        source=Path(self.translate_path(urlsplit(self.path).path))
        packed=Path(str(source)+".gz")
        if "gzip" in self.headers.get("Accept-Encoding","") and source.is_file() and packed.is_file() and packed.stat().st_mtime>=source.stat().st_mtime:
            asset=urlsplit(self.path);self.path=asset.path+".gz"+(("?"+asset.query) if asset.query else "")
        if self.command=="HEAD":
            result=self.send_head()
            if result: result.close()
        else: super().do_GET()
    def do_HEAD(self):
        self.do_GET()
    def redirect(self, kind, query):
        values = dict(parse_qsl(query, keep_blank_values=True))
        host = urlsplit("http://" + self.headers.get("Host", "127.0.0.1")).hostname or "127.0.0.1"
        values.setdefault("host", host); values.setdefault("port", str(self.server.game_port))
        self.send_response(307); self.send_header("Location", "/"+kind+"/?"+urlencode(values)); self.send_header("Content-Length", "0"); self.end_headers()
    def end_headers(self):
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Vary", "Accept-Encoding")
        path = Path(self.translate_path(urlsplit(self.path).path))
        if path.is_file() and path.suffix in (".gz", ".br"):
            self.send_header("Content-Encoding", "gzip" if path.suffix==".gz" else "br")
        super().end_headers()

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("demo",nargs="?",default="soccer",choices=("soccer","m2"))
    parser.add_argument("--port",type=int,default=8094)
    parser.add_argument("--game-port",type=int,default=7777)
    args=parser.parse_args()
    global BUILDS
    prefix="M2" if args.demo=="m2" else "Soccer"
    BUILDS={"desktop":ROOT/(prefix+"-Desktop"),"mobile":ROOT/(prefix+"-Mobile")}
    if not all(1<=p<=65535 for p in (args.port,args.game_port)): parser.error("Ports must be 1-65535")
    if not any((b/"index.html").is_file() for b in BUILDS.values()): parser.error("No Soccer builds found next to this script")
    # Precompress existing build files without slowing Unity's development build pipeline.
    for build in BUILDS.values():
        for source in (build/"Build").glob("*"):
            if source.suffix not in (".wasm",".data",".js"): continue
            packed=Path(str(source)+".gz"); stamp=source.stat().st_mtime
            if packed.exists() and packed.stat().st_mtime>=stamp: continue
            temporary=Path(str(packed)+".tmp")
            with source.open("rb") as raw, temporary.open("wb") as output:
                with gzip.GzipFile(filename="",fileobj=output,mode="wb",compresslevel=1,mtime=int(stamp)) as compressed:
                    shutil.copyfileobj(raw,compressed,1024*1024)
            if source.stat().st_mtime==stamp: temporary.replace(packed)
            else: temporary.unlink()
    with http.server.ThreadingHTTPServer(("0.0.0.0",args.port),functools.partial(Handler,directory=str(ROOT))) as server:
        server.daemon_threads=True; server.game_port=args.game_port
        print("Open M2_Launcher in Unity." if args.demo=="m2" else "Open 08_Soccer in Unity.",flush=True)
        print("Then click Host or Start server only.",flush=True)
        addresses={"127.0.0.1"}
        try: addresses.update(e[4][0] for e in socket.getaddrinfo(socket.gethostname(),None,socket.AF_INET))
        except OSError: pass
        for address in sorted(addresses):
            if ipaddress.ip_address(address).is_link_local: continue
            print(f"Auto: http://{address}:{args.port}/",flush=True)
            print(f"Desktop: http://{address}:{args.port}/desktop/",flush=True)
            print(f"Mobile: http://{address}:{args.port}/mobile/",flush=True)
        print("Keep this window open. Ctrl+C stops serving. Firewall: run Enable-Firewall.cmd once as administrator.",flush=True)
        try: server.serve_forever()
        except KeyboardInterrupt: print("Stopped.",flush=True)

if __name__=="__main__": main()
