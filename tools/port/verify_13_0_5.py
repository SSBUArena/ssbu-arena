"""The checks of verify_build.py for 13.0.5 (kept under its old name):  python tools/port/verify_13_0_5.py <13.0.5 main.elf> <functions.csv>"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import verify_build  # noqa: E402

if __name__ == '__main__':
    verify_build.main(sys.argv[1:] + ['--build', '13.0.5'])
