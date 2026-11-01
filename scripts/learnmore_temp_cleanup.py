#!/usr/bin/env python3
"""清理明確登記的退役建置產物；預設只預覽，不掃除整個 /tmp。"""
import argparse
import fcntl
import json
import math
import os
from pathlib import Path
import re
import stat
import subprocess
import time

KEEP_SUFFIXES = {'.json', '.jsonl', '.log', '.md', '.txt', '.py', '.sh', '.ps1', '.config',
                 '.yaml', '.yml', '.toml', '.ini', '.conf', '.xml', '.sql',
                 '.pem', '.key', '.pfx', '.p12', '.cer', '.crt'}
KEEP_NAMES = {'model-cache', 'models', 'venv', '.venv', 'node_modules', '.keep'}
MODEL_SUFFIXES = {'.pt', '.pth', '.ckpt', '.safetensors', '.onnx', '.bin'}


def within(path, root):
    return path == root or root in path.parents


def load_registry(path):
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid() or info.st_mode & 0o022:
        raise ValueError('registry_permissions')
    value = json.loads(path.read_text())
    roots = value.get('retired_publish_roots')
    if not isinstance(roots, list) or not roots or len(roots) != len(set(roots)):
        raise ValueError('invalid_registry')
    paths = [Path(p) for p in roots]
    if any(p.parent != Path('/tmp') or not re.fullmatch(r'learnmore-[a-z0-9-]+-publish', p.name)
           for p in paths):
        raise ValueError('root_outside_policy')
    return paths


def docker_mounts():
    # 查詢僅輸出掛載資訊，避免讀取或記錄容器環境變數。
    result = subprocess.run(['docker', 'ps', '-aq'], check=True, capture_output=True, text=True, timeout=30)
    ids = result.stdout.split()
    if not ids:
        return []
    result = subprocess.run(['docker', 'inspect', '--format', '{{json .Mounts}}', *ids],
                            check=True, capture_output=True, text=True, timeout=30)
    return [Path(m['Source']) for line in result.stdout.splitlines()
            for m in json.loads(line) if m.get('Source')]


def mounted_paths():
    def unescape(s):
        return re.sub(r'\\([0-7]{3})', lambda m: chr(int(m[1], 8)), s)
    return [Path(unescape(line.split()[4])) for line in Path('/proc/self/mountinfo').read_text().splitlines()]


def busy_roots(roots):
    busy = set()
    for proc in Path('/proc').glob('[0-9]*'):
        try:
            if int(proc.name) == os.getpid() or proc.stat().st_uid != os.getuid():
                continue
            # 只在記憶體比對，不輸出指令列或其他程序資料。
            command = (proc / 'cmdline').read_bytes()
            for root in roots:
                if str(root).encode() in command:
                    busy.add(root)
            try:
                descriptors = list((proc / 'fd').iterdir())
            except PermissionError:
                descriptors = []  # 系統憑證代理等受保護程序仍可比對指令列。
            for link in [proc / 'cwd', *descriptors]:
                try:
                    target = Path(os.readlink(link))
                    busy.update(root for root in roots if within(target, root))
                except OSError:
                    continue
            try:
                maps = (proc / 'maps').read_text()
            except PermissionError:
                maps = ''
            busy.update(root for root in roots if str(root) in maps)
        except FileNotFoundError:
            continue  # 程序可能剛好結束。
        except (PermissionError, ProcessLookupError):
            # 無法檢查同帳戶程序時，寧可保留全部候選。
            busy.update(roots)
    return busy


def cleanup_root(root, cutoff, apply=False, protected=(), busy=()):
    result = {'root': root.name, 'mode': 'apply' if apply else 'preview',
              'files': 0, 'allocatedBytes': 0, 'skipped': 0}
    if root in busy or any(within(root, p) or within(p, root) for p in protected):
        return dict(result, reason='active_or_mounted')
    try:
        info = root.lstat()
    except FileNotFoundError:
        return dict(result, reason='missing')
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid() or root.resolve() != root:
        return dict(result, reason='unsafe_root')

    def contains_git(path):
        with os.scandir(path) as entries:
            for entry in entries:
                if entry.name in {'.git', '.keep', 'pyvenv.cfg'}:
                    return True
                if entry.is_dir(follow_symlinks=False) and contains_git(Path(entry.path)):
                    return True
        return False

    if contains_git(root):
        return dict(result, reason='repository_or_keep_marker')

    def walk(fd):
        if {'.git', '.keep', 'pyvenv.cfg'} & set(os.listdir(fd)):
            raise RuntimeError('protected_marker_created')
        with os.scandir(fd) as entries:
            for entry in entries:
                current = os.stat(entry.name, dir_fd=fd, follow_symlinks=False)
                if stat.S_ISDIR(current.st_mode):
                    if entry.name in KEEP_NAMES or current.st_dev != info.st_dev:
                        result['skipped'] += 1
                        continue
                    child = os.open(entry.name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
                    try:
                        walk(child)
                    finally:
                        os.close(child)
                    continue
                name = Path(entry.name)
                if (not stat.S_ISREG(current.st_mode) or current.st_uid != os.getuid()
                        or current.st_nlink != 1 or name.suffix.lower() in KEEP_SUFFIXES | MODEL_SUFFIXES
                        or name.name.startswith('.') or name.name.lower().startswith('appsettings')
                        or max(current.st_atime, current.st_mtime, current.st_ctime) > cutoff):
                    result['skipped'] += 1
                    continue
                latest = os.stat(entry.name, dir_fd=fd, follow_symlinks=False)
                if ((latest.st_dev, latest.st_ino, latest.st_size, latest.st_ctime_ns)
                        != (current.st_dev, current.st_ino, current.st_size, current.st_ctime_ns)):
                    result['skipped'] += 1
                    continue
                if apply:
                    os.unlink(entry.name, dir_fd=fd)
                result['files'] += 1
                result['allocatedBytes'] += current.st_blocks * 512

    fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        opened = os.fstat(fd)
        if (opened.st_dev, opened.st_ino) != (info.st_dev, info.st_ino):
            return dict(result, reason='root_changed')
        walk(fd)
    finally:
        os.close(fd)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--registry', type=Path, required=True)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--min-age-days', type=float, default=7)
    args = parser.parse_args()
    if not math.isfinite(args.min_age_days) or args.min_age_days < 0:
        parser.error('min-age-days must not be negative')
    state = Path.home() / '.local/state/learnmore-temp-cleanup'
    state.mkdir(parents=True, exist_ok=True, mode=0o700)
    try:
        with (state / 'cleanup.lock').open('a') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            roots = load_registry(args.registry)
            protected = docker_mounts() + [p for p in mounted_paths()
                                           if any(within(p, root) for root in roots)]
            busy = busy_roots(roots)
            report = {'minAgeDays': args.min_age_days, 'results': [
                cleanup_root(root, time.time() - args.min_age_days * 86400,
                             args.apply, protected, busy) for root in roots]}
            print(json.dumps(report, ensure_ascii=False))
            receipt = state / ('last-apply.json' if args.apply else 'last-preview.json')
            receipt.write_text(json.dumps(report, ensure_ascii=False, indent=2))
    except Exception as exc:
        # 不將 Docker 錯誤、指令列或設定內容寫入日誌。
        print(json.dumps({'error': type(exc).__name__, 'cleanupCompleted': False}))
        raise SystemExit(1)


if __name__ == '__main__':
    main()
