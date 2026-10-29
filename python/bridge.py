"""Pinned ARFT 0.6.0 adapter. The C# engine owns the plan, names and durable queue.

ARFT's full CLI can replace final files and has no per-file name resolver. Use its
tested pull primitive only on dedicated staging files; C# commits without overwrite.
"""
import argparse
import json
import logging
import os
from pathlib import Path
import sys


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--adb')
    parser.add_argument('--serial')
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    from importlib.metadata import version
    from arft.cli import RemoteFile, pull_one_file
    if version('arft') != '0.6.0':
        raise RuntimeError('Ce pont exige arft==0.6.0')
    if args.check:
        print('ARFT 0.6.0 et son pont sont opérationnels')
        return
    if not args.adb or not args.serial:
        parser.error('--adb et --serial sont requis')
    # All subprocesses spawned by ARFT use the explicitly selected device.
    os.environ['ANDROID_SERIAL'] = args.serial
    logger = logging.getLogger('androcp.arft')
    logger.addHandler(logging.StreamHandler(sys.stderr))
    logger.setLevel(logging.INFO)
    for line in sys.stdin:
        try:
            request = json.loads(line)
            target = Path(request['staging'])
            if target.exists() or target.is_symlink() or target.with_name(target.name + '.part').is_symlink():
                raise RuntimeError('Chemin de staging déjà occupé ou lié')
            ok, reason = pull_one_file(args.adb, request['remote'], target,
                                      RemoteFile(request['remote'], size=request['size']),
                                      logger=logger, verify_hash=False, hash_cmd=None,
                                      per_file_retries=1, retry_wait=0)
            print(json.dumps({'ok': ok, 'error': reason}), flush=True)
        except Exception as exc:
            print(json.dumps({'ok': False, 'error': str(exc)}), flush=True)


if __name__ == '__main__':
    main()
