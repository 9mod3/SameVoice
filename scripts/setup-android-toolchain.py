"""Download and unpack a JDK 17 and the Android command-line tools into .toolchain."""

from __future__ import annotations

import os
import shutil
import sys
import time
import urllib.request
import zipfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
TOOLCHAIN = os.path.join(ROOT, ".toolchain")

JDK_URL = ("https://mirrors.tuna.tsinghua.edu.cn/Adoptium/17/jdk/x64/windows/"
           "OpenJDK17U-jdk_x64_windows_hotspot_17.0.21_9.zip")
SDK_URL = "https://dl.google.com/android/repository/commandlinetools-win-13114758_latest.zip"


def download(url: str, path: str) -> None:
    part = path + ".part"
    for attempt in range(4):
        try:
            with urllib.request.urlopen(url, timeout=60) as response:
                total = int(response.headers.get("Content-Length") or 0)
                done = 0
                last = 0.0
                with open(part, "wb") as out:
                    while True:
                        chunk = response.read(1 << 20)
                        if not chunk:
                            break
                        out.write(chunk)
                        done += len(chunk)
                        now = time.time()
                        if now - last > 2:
                            last = now
                            pct = (done * 100.0 / total) if total else 0
                            print(f"  {os.path.basename(path)} {done/1e6:.1f}/{total/1e6:.1f} MB ({pct:.0f}%)",
                                  flush=True)
            os.replace(part, path)
            return
        except Exception as exc:  # noqa: BLE001
            print(f"  retry {attempt + 1} for {url}: {exc}", flush=True)
            time.sleep(2)
    raise SystemExit(f"download failed: {url}")


def extract(zip_path: str, dest: str) -> None:
    with zipfile.ZipFile(zip_path) as archive:
        archive.extractall(dest)


def main() -> int:
    os.makedirs(TOOLCHAIN, exist_ok=True)

    jdk_zip = os.path.join(TOOLCHAIN, "jdk.zip")
    jdk_dir = os.path.join(TOOLCHAIN, "jdk")
    if not os.path.isfile(os.path.join(jdk_dir, "bin", "java.exe")):
        print("downloading JDK 17...", flush=True)
        download(JDK_URL, jdk_zip)
        print("extracting JDK...", flush=True)
        tmp = os.path.join(TOOLCHAIN, "_jdk_tmp")
        shutil.rmtree(tmp, ignore_errors=True)
        extract(jdk_zip, tmp)
        entries = [e for e in os.listdir(tmp) if os.path.isdir(os.path.join(tmp, e))]
        src = os.path.join(tmp, entries[0]) if len(entries) == 1 else tmp
        shutil.rmtree(jdk_dir, ignore_errors=True)
        shutil.move(src, jdk_dir)
        shutil.rmtree(tmp, ignore_errors=True)
        os.remove(jdk_zip)
        print("JDK ready:", jdk_dir, flush=True)

    sdk_zip = os.path.join(TOOLCHAIN, "cmdline-tools.zip")
    sdk_root = os.path.join(TOOLCHAIN, "android-sdk")
    latest = os.path.join(sdk_root, "cmdline-tools", "latest")
    if not os.path.isfile(os.path.join(latest, "bin", "sdkmanager.bat")):
        print("downloading Android command-line tools...", flush=True)
        download(SDK_URL, sdk_zip)
        print("extracting command-line tools...", flush=True)
        tmp = os.path.join(TOOLCHAIN, "_sdk_tmp")
        shutil.rmtree(tmp, ignore_errors=True)
        extract(sdk_zip, tmp)
        os.makedirs(os.path.dirname(latest), exist_ok=True)
        shutil.rmtree(latest, ignore_errors=True)
        shutil.move(os.path.join(tmp, "cmdline-tools"), latest)
        shutil.rmtree(tmp, ignore_errors=True)
        os.remove(sdk_zip)
        print("sdkmanager ready:", latest, flush=True)

    print("DONE", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
