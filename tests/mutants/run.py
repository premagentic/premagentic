#!/usr/bin/env python3
"""Break the code on purpose, one change at a time, and watch a test fail.

Each mutant in a list is a one-place change to the product's source. The
runner applies it, builds, runs the tests that can reach it, records which
tests failed, and puts the file back. A guard that no test notices when it is
broken is not guarded, and this is how that is found.

    python tests/mutants/run.py --check tests/mutants/*.json
    python tests/mutants/run.py tests/mutants/<list>.json
    python tests/mutants/run.py tests/mutants/<list>.json --only "<mutant name>" ...
    python tests/mutants/run.py --restore
    python tests/mutants/run.py --self-test

Python 3.8 or later, standard library only. README.md beside this file
describes the list format and every rule below.
"""
import argparse
import datetime
import glob
import json
import os
import re
import signal
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_REPO = os.path.dirname(os.path.dirname(HERE))
DEFAULT_PROJECT = "tests/Premagentic.Tests/Premagentic.Tests.csproj"
BACKUP = ".mutant-backup"
SKIP_DIRS = {".git", "bin", "obj", "node_modules", "runs"}
PLATFORMS = ("linux", "windows")
LIST_KEYS = {"name", "commit", "platform", "project", "mutants"}
MUTANT_KEYS = {"name", "file", "old", "new", "edits", "filter", "expect", "platform"}
EDIT_KEYS = {"file", "old", "new"}

# Exit codes. Only 0 means every mutant that ran was caught between two
# passing controls.
EXIT_OK = 0
EXIT_NOT_ALL_CAUGHT = 1
EXIT_BAD_LIST = 2
EXIT_SIDECAR = 3
EXIT_LOW_MEMORY = 4
EXIT_NOTHING_RAN = 5
EXIT_CONTROL_BEFORE = 6

SUMMARY_LINE = re.compile(
    r"^(Passed|Failed)!\s+-\s+Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)", re.M)
# The console logger's line for a failed test, and xUnit's own. Either names it.
VSTEST_FAILED = re.compile(r"^  Failed (?!\!)(.+)$", re.M)
XUNIT_FAILED = re.compile(r"^\[xUnit\.net [^\]]*\]\s+(.+) \[FAIL\]\s*$", re.M)
CRASH = re.compile(r"Test host process crashed|The active test run was aborted|Test Run Aborted")
NO_MATCH = "No test matches the given testcase filter"
BUILD_ERROR = re.compile(r"^.*: error [A-Z]+[0-9]+:.*$", re.M)


# ---------------------------------------------------------------- the list

class ListError(Exception):
    pass


def this_platform():
    if sys.platform.startswith("linux"):
        return "linux"
    if sys.platform.startswith("win"):
        return "windows"
    return sys.platform


def load_list(path):
    """The list as JSON, with its shape checked. Unknown keys are refused, so a
    list written in another runner's format fails here, before it runs."""
    try:
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
    except (OSError, ValueError) as e:
        raise ListError(f"{path}: cannot be read as JSON: {e}")
    problems = []
    if not isinstance(data, dict):
        raise ListError(f"{path}: a list is a JSON object with name, commit and mutants")
    for key in sorted(set(data) - LIST_KEYS):
        problems.append(f"unknown key '{key}' at the top")
    for key in ("name", "commit"):
        if not isinstance(data.get(key), str) or not data.get(key).strip():
            problems.append(f"'{key}' is missing or empty")
    if data.get("platform") is not None and data["platform"] not in PLATFORMS:
        problems.append(f"'platform' is '{data['platform']}', expected one of {', '.join(PLATFORMS)}")
    if data.get("project") is not None and not isinstance(data["project"], str):
        problems.append("'project' is not a string")
    mutants = data.get("mutants")
    if not isinstance(mutants, list) or not mutants:
        problems.append("'mutants' is missing or empty")
        mutants = []
    seen = set()
    for i, m in enumerate(mutants, 1):
        where = f"mutant {i}"
        if not isinstance(m, dict):
            problems.append(f"{where} is not an object")
            continue
        name = m.get("name")
        if not isinstance(name, str) or not name.strip():
            problems.append(f"{where} has no name")
        else:
            where = f"mutant '{name}'"
            if any(ord(c) < 32 for c in name):
                problems.append(f"{where}: a name is one line with no control characters")
            if name in seen:
                problems.append(f"{where}: the name is used twice; the log is read by name")
            seen.add(name)
        for key in sorted(set(m) - MUTANT_KEYS):
            problems.append(f"{where}: unknown key '{key}'")
        if not isinstance(m.get("filter"), str) or not m.get("filter").strip():
            problems.append(f"{where}: 'filter' is missing; name the tests that can reach the change")
        if m.get("expect") is not None and m["expect"] != "crash":
            problems.append(f"{where}: 'expect' may only be 'crash'")
        if m.get("platform") is not None and m["platform"] not in PLATFORMS:
            problems.append(f"{where}: 'platform' is '{m['platform']}', expected one of {', '.join(PLATFORMS)}")
        has_single = any(k in m for k in ("file", "old", "new"))
        if has_single and "edits" in m:
            problems.append(f"{where}: give either file, old and new, or edits, not both")
        elif "edits" in m:
            if not isinstance(m["edits"], list) or len(m["edits"]) < 2:
                problems.append(f"{where}: 'edits' is a list of two or more changes made together")
            else:
                for j, e in enumerate(m["edits"], 1):
                    if not isinstance(e, dict):
                        problems.append(f"{where}: edit {j} is not an object")
                        continue
                    for key in sorted(set(e) - EDIT_KEYS):
                        problems.append(f"{where}: edit {j} has unknown key '{key}'")
                    problems += [f"{where}: edit {j}: {p}" for p in edit_shape_problems(e)]
        else:
            problems += [f"{where}: {p}" for p in edit_shape_problems(m)]
    if problems:
        raise ListError(f"{path}:\n  " + "\n  ".join(problems))
    return data


def edit_shape_problems(e):
    problems = []
    for key in ("file", "old", "new"):
        if not isinstance(e.get(key), str):
            problems.append(f"'{key}' is missing or not a string")
    if problems:
        return problems
    if not e["old"]:
        problems.append("'old' is empty")
    if e["old"] == e["new"]:
        problems.append("'old' and 'new' are the same text")
    f = e["file"]
    if os.path.isabs(f) or f.startswith(("/", "\\")) or ".." in re.split(r"[\\/]", f):
        problems.append(f"'file' must be a path inside the repository, relative to its root: {f}")
    return problems


def edits_of(m):
    return m["edits"] if "edits" in m else [{"file": m["file"], "old": m["old"], "new": m["new"]}]


def platform_of(lst, m):
    return m.get("platform") or lst.get("platform")


def project_of(lst):
    return lst.get("project") or DEFAULT_PROJECT


# ------------------------------------------------------------- the anchors

def read_bytes(path):
    with open(path, "rb") as f:
        return f.read()


def apply_edits(original, edits_for_file):
    """The file's text with each edit made in turn, keeping the file's own line
    endings. An 'old' is written with \\n and must occur exactly once. Raises
    ListError with the reason when it does not."""
    text = original.decode("utf-8")
    crlf = "\r\n" in text
    if crlf and "\n" in text.replace("\r\n", ""):
        raise ListError("the file mixes CRLF and LF line endings, so an anchor cannot be placed exactly")
    for e in edits_for_file:
        old, new = e["old"].replace("\r\n", "\n"), e["new"].replace("\r\n", "\n")
        if crlf:
            old, new = old.replace("\n", "\r\n"), new.replace("\n", "\r\n")
        count = text.count(old)
        if count != 1:
            raise ListError(f"'old' occurs {count} times in {e['file']}; it must occur exactly once")
        text = text.replace(old, new, 1)
    return text.encode("utf-8")


def by_file(edits):
    grouped = {}
    for e in edits:
        grouped.setdefault(e["file"].replace("\\", "/"), []).append(e)
    return grouped


def check_anchors(repo, lst):
    """Every problem with the list's anchors on today's tree, read-only."""
    problems = []
    project = project_of(lst)
    if not os.path.isfile(os.path.join(repo, project)):
        problems.append(f"the test project {project} does not exist")
    for m in lst["mutants"]:
        for rel, edits in by_file(edits_of(m)).items():
            path = os.path.join(repo, rel)
            if not os.path.isfile(path):
                problems.append(f"{m['name']}: {rel} does not exist")
                continue
            try:
                apply_edits(read_bytes(path), edits)
            except ListError as e:
                problems.append(f"{m['name']}: {e}")
            except UnicodeDecodeError:
                problems.append(f"{m['name']}: {rel} is not UTF-8 text")
    return problems


# -------------------------------------------------------------- sidecars

def sidecars(repo):
    """Every sidecar in the tree: each one is a file a run changed and did not
    put back."""
    found = []
    for root, dirs, names in os.walk(repo):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        found += [os.path.join(root, n) for n in names if n.endswith(BACKUP)]
    return sorted(found)


def put_back(sidecar):
    """Writes the sidecar's bytes over the file it names, checks them byte for
    byte, touches the file so no incremental build skips it, and only then
    deletes the sidecar. Returns the file's path."""
    path = sidecar[: -len(BACKUP)]
    original = read_bytes(sidecar)
    with open(path, "wb") as f:
        f.write(original)
    os.utime(path, None)
    if read_bytes(path) != original:
        raise RuntimeError(f"the restore of {path} did not land; its sidecar is kept")
    os.remove(sidecar)
    return path


def mutate(repo, m):
    """Writes each touched file's sidecar, checked, before the file is changed.
    Returns the sidecars written, so the caller restores exactly those."""
    written = []
    try:
        for rel, edits in by_file(edits_of(m)).items():
            path = os.path.normpath(os.path.join(repo, rel))
            original = read_bytes(path)
            mutated = apply_edits(original, edits)
            sidecar = path + BACKUP
            if os.path.exists(sidecar):
                raise RuntimeError(f"a sidecar already exists for {rel}")
            with open(sidecar, "wb") as f:
                f.write(original)
                f.flush()
                os.fsync(f.fileno())
            if read_bytes(sidecar) != original:
                raise RuntimeError(f"the sidecar for {rel} did not land")
            written.append(sidecar)
            with open(path, "wb") as f:
                f.write(mutated)
    except Exception:
        for s in written:
            put_back(s)
        raise
    return written


# ------------------------------------------------------------ processes

def child_env():
    env = dict(os.environ)
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_NOLOGO"] = "1"
    return env


# How long a run stopped at its time limit is given to close its output once
# its process tree is ended. A process outside the tree can hold the output
# open: a grandchild whose parent had already exited, which the tree no longer
# names, or one the kill could not end. The runner stops waiting for it rather
# than wait past its own limit.
KILL_GRACE_S = 10


def run_process(cmd, cwd, timeout_s):
    """(exit code, output, timed out). The exit code is the child's own, never
    a pipeline's, and the output is taken only once the stream has ended, not
    when the process exits, so no last line is lost. A run past its limit is
    ended with its tree and given KILL_GRACE_S more, never longer."""
    kwargs = {}
    if os.name == "nt":
        kwargs["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
    else:
        kwargs["start_new_session"] = True
    proc = subprocess.Popen(cmd, cwd=cwd, env=child_env(), stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, **kwargs)
    try:
        out, _ = proc.communicate(timeout=timeout_s)
        return proc.returncode, out.decode("utf-8", errors="replace"), False
    except subprocess.TimeoutExpired:
        kill_tree(proc)
        try:
            out, _ = proc.communicate(timeout=KILL_GRACE_S)
        except subprocess.TimeoutExpired:
            out = (b"(the runner stopped waiting for this output: a process outside the ended tree "
                   b"still held it open)\n")
            try:
                proc.wait(timeout=KILL_GRACE_S)
            except subprocess.TimeoutExpired:
                pass
        return proc.returncode, out.decode("utf-8", errors="replace"), True


def kill_tree(proc):
    if os.name == "nt":
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(proc.pid)],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    else:
        try:
            os.killpg(proc.pid, signal.SIGKILL)
        except OSError:
            pass


def free_gb():
    """Free physical memory in GB, or None where it cannot be read."""
    if os.name == "nt":
        import ctypes

        class MemoryStatus(ctypes.Structure):
            _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong),
                        ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                        ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong),
                        ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong),
                        ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]

        status = MemoryStatus()
        status.dwLength = ctypes.sizeof(status)
        if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
            return None
        return status.ullAvailPhys / (1 << 30)
    try:
        with open("/proc/meminfo") as f:
            for line in f:
                if line.startswith("MemAvailable:"):
                    return int(line.split()[1]) / (1 << 20)
    except OSError:
        pass
    return None


# ---------------------------------------------------------- the verdicts

def test_name(display):
    """A failed test's name from its display name. A theory's display name
    carries its arguments, which may hold dots and any text, so it is cut at
    the first '(' before anything else; a plain test's ' [12 ms]' goes too."""
    name = display.split("(", 1)[0]
    name = name.split(" [", 1)[0].strip()
    return name


def failed_tests(output):
    names = {test_name(m) for m in VSTEST_FAILED.findall(output)}
    names |= {test_name(m) for m in XUNIT_FAILED.findall(output)}
    return sorted(n for n in names if n)


def read_summary(output):
    """(failed, passed, total) from the run's last summary line, or None."""
    found = SUMMARY_LINE.findall(output)
    if not found:
        return None
    _, failed, passed, _, total = found[-1]
    return int(failed), int(passed), int(total)


def build_ok(code, output):
    return code == 0 and "Build succeeded" in output


def first_build_error(output):
    m = BUILD_ERROR.search(output)
    return m.group(0).strip()[:300] if m else "no error line; see the build output"


def judge(build_code, build_output, test_code, test_output, timed_out, expect_crash):
    """(verdict, detail lines). CAUGHT only when the build succeeded and a named
    test failed, or, for a mutant that expects it, the test host died.
    Everything else is SURVIVED or NO VERDICT, with the reason."""
    if not build_ok(build_code, build_output):
        return "NO VERDICT", [f"reason: the build failed: {first_build_error(build_output)}"]
    if timed_out:
        return "NO VERDICT", ["reason: the test run passed its time limit and was stopped"]
    failed = failed_tests(test_output)
    summary = read_summary(test_output)
    crashed = bool(CRASH.search(test_output))
    if failed and test_code != 0:
        return "CAUGHT", [f"failed: {n}" for n in failed]
    if crashed and test_code != 0:
        if expect_crash:
            return "CAUGHT", ["failed: the test host died, as this mutant expects"]
        return "NO VERDICT", ["reason: the test host died, and this mutant does not expect a crash"]
    if NO_MATCH in test_output:
        return "NO VERDICT", ["reason: the filter reached no test"]
    if summary is None:
        return "NO VERDICT", [f"reason: the test run printed no summary (exit {test_code})"]
    f, p, total = summary
    if f > 0:
        return "NO VERDICT", [f"reason: {f} failed but no failed test could be named (exit {test_code})"]
    if total == 0 or p == 0:
        return "NO VERDICT", ["reason: no test ran"]
    if test_code != 0:
        return "NO VERDICT", [f"reason: every test passed but the run exited {test_code}"]
    return "SURVIVED", [f"tests: {p} passed, 0 failed"]


def control_passed(build_code, build_output, test_code, test_output, timed_out):
    """(passed, detail). A control passes only when it built, ran at least one
    test, and every test passed."""
    if not build_ok(build_code, build_output):
        return False, f"the build failed: {first_build_error(build_output)}"
    if timed_out:
        return False, "the test run passed its time limit"
    summary = read_summary(test_output)
    if summary is None:
        return False, f"no summary (exit {test_code})"
    f, p, total = summary
    if f or test_code != 0 or total == 0 or p == 0:
        return False, f"{p} passed, {f} failed, exit {test_code}"
    return True, f"{p} passed, 0 failed"


# ----------------------------------------------------------------- the log

VERDICT_LINE = re.compile(r"^VERDICT (CAUGHT|SURVIVED|NO VERDICT|SKIPPED): (.+)$")
CONTROL_LINE = re.compile(r"^CONTROL (BEFORE|AFTER): (PASSED|FAILED)\b")


class Log:
    def __init__(self, path):
        self.path = path
        self.file = open(path, "a", encoding="utf-8", newline="\n")

    def line(self, text=""):
        self.file.write(text + "\n")
        self.file.flush()
        print(text, flush=True)

    def close(self):
        self.file.close()


def read_log(path, names):
    """What the log says, read back from the file: each mutant's verdict, the
    two controls, and the counts. Nothing here comes from the run's memory."""
    verdicts, controls, stopped = {}, {}, False
    with open(path, encoding="utf-8") as f:
        for raw in f:
            line = raw.rstrip("\n")
            m = VERDICT_LINE.match(line)
            if m:
                verdicts.setdefault(m.group(2), m.group(1))
                continue
            m = CONTROL_LINE.match(line)
            if m:
                controls[m.group(1)] = m.group(2)
            if line.startswith("STOPPED "):
                stopped = True
    counts = {"CAUGHT": 0, "SURVIVED": 0, "NO VERDICT": 0, "SKIPPED": 0, "NOT RUN": 0}
    missing = []
    for n in names:
        v = verdicts.get(n)
        if v is None:
            counts["NOT RUN"] += 1
            missing.append(n)
        else:
            counts[v] += 1
    return counts, controls, stopped, missing


def exit_code_from_log(counts, controls, stopped):
    ran = counts["CAUGHT"] + counts["SURVIVED"] + counts["NO VERDICT"]
    if controls.get("BEFORE") == "FAILED":
        return EXIT_CONTROL_BEFORE
    if stopped:
        return EXIT_LOW_MEMORY
    if ran == 0:
        return EXIT_NOTHING_RAN
    if (counts["SURVIVED"] or counts["NO VERDICT"] or counts["NOT RUN"]
            or controls.get("BEFORE") != "PASSED" or controls.get("AFTER") != "PASSED"):
        return EXIT_NOT_ALL_CAUGHT
    return EXIT_OK


# ------------------------------------------------------------------ a run

def git_line(repo):
    """The tree's commit and how many files differ from it, or unknown."""
    try:
        head = subprocess.run(["git", "-C", repo, "rev-parse", "--short", "HEAD"],
                              capture_output=True, text=True)
        status = subprocess.run(["git", "-C", repo, "status", "--porcelain"],
                                capture_output=True, text=True)
    except OSError:
        return "unknown", None
    if head.returncode != 0 or status.returncode != 0:
        return "unknown", None
    return head.stdout.strip(), len([l for l in status.stdout.splitlines() if l.strip()])


def run_list(repo, list_path, only, min_free_gb, out_dir, timeout_s):
    try:
        lst = load_list(list_path)
    except ListError as e:
        print(f"LIST REFUSED: {e}")
        return EXIT_BAD_LIST

    left = sidecars(repo)
    if left:
        print("REFUSING TO START: a previous run did not put these files back, so the tree may")
        print("still hold a mutant, and a control run on it would pass on mutated code.")
        for s in left:
            print(f"    {os.path.relpath(s[: -len(BACKUP)], repo)}")
        print("Each sidecar holds the file as it was. Put them back with:")
        print("    python tests/mutants/run.py --restore")
        print("then check `git status`, and run again.")
        return EXIT_SIDECAR

    problems = check_anchors(repo, lst)
    if problems:
        print("ANCHORS REFUSED; nothing was changed:")
        for p in problems:
            print(f"    {p}")
        return EXIT_BAD_LIST

    mutants = lst["mutants"]
    if only:
        unknown = [n for n in only if n not in {m["name"] for m in mutants}]
        if unknown:
            print("NO SUCH MUTANT: " + "; ".join(unknown))
            return EXIT_BAD_LIST
        mutants = [m for m in mutants if m["name"] in only]
    names = [m["name"] for m in mutants]
    here = this_platform()
    runnable = [m for m in mutants if platform_of(lst, m) in (None, here)]
    project = project_of(lst)

    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    stem = os.path.splitext(os.path.basename(list_path))[0]
    folder = os.path.join(out_dir or os.path.join(HERE, "runs"), f"{stem}-{stamp}")
    os.makedirs(folder, exist_ok=True)
    log = Log(os.path.join(folder, "run.log"))
    head, changed = git_line(repo)
    log.line(f"LIST {lst['name']} ({os.path.basename(list_path)})")
    log.line(f"LIST COMMIT {lst['commit']}; TREE {head}" +
             ("" if changed is None else f", {changed} changed file(s) in the working tree"))
    log.line(f"PLATFORM {here}; PROJECT {project}; {len(runnable)} of {len(mutants)} to run here")

    for m in mutants:
        if m not in runnable:
            log.line(f"VERDICT SKIPPED: {m['name']}")
            log.line(f"    reason: this mutant is for {platform_of(lst, m)}, and this machine is {here}")

    def memory_ok(before):
        free = free_gb()
        log.line(f"MEMORY before {before}: {'unknown' if free is None else f'{free:.1f} GB free'}")
        if min_free_gb and (free is None or free < min_free_gb):
            log.line(f"STOPPED before {before}: free memory is under {min_free_gb} GB or cannot be read;"
                     " every file is as the repository holds it")
            return False
        return True

    build_cmd = ["dotnet", "build", project, "-c", "Release", "--no-incremental",
                 "-nologo", "-nodeReuse:false", "-tl:off"]

    def test_cmd(test_filter):
        return ["dotnet", "test", project, "-c", "Release", "--no-build", "--filter", test_filter]

    def save(name, text):
        with open(os.path.join(folder, name), "w", encoding="utf-8", newline="\n") as f:
            f.write(text)

    def control(which):
        union = "|".join(f"({m['filter']})" for m in runnable)
        bc, bo, bt = run_process(build_cmd, repo, timeout_s)
        save(f"control-{which.lower()}-build.txt", bo)
        if build_ok(bc, bo) and not bt:
            tc, to, tt = run_process(test_cmd(union), repo, timeout_s)
            save(f"control-{which.lower()}-test.txt", to)
        else:
            tc, to, tt = 1, "", False
        ok, detail = control_passed(bc, bo, tc, to, tt or bt)
        log.line(f"CONTROL {which}: {'PASSED' if ok else 'FAILED'} {detail}")
        return ok

    stopped = False
    try:
        if runnable:
            if not memory_ok("the control"):
                stopped = True
            elif control("BEFORE"):
                for i, m in enumerate(runnable, 1):
                    if not memory_ok(m["name"]):
                        stopped = True
                        break
                    log.line(f"MUTANT {i}/{len(runnable)}: {m['name']}")
                    written = mutate(repo, m)
                    try:
                        bc, bo, bt = run_process(build_cmd, repo, timeout_s)
                        save(f"{i:03d}-build.txt", bo)
                        if build_ok(bc, bo) and not bt:
                            tc, to, tt = run_process(test_cmd(m["filter"]), repo, timeout_s)
                            save(f"{i:03d}-test.txt", to)
                        else:
                            tc, to, tt = 1, "", bt
                    finally:
                        for s in written:
                            put_back(s)
                    verdict, details = judge(bc, bo, tc, to, tt, m.get("expect") == "crash")
                    log.line(f"VERDICT {verdict}: {m['name']}")
                    for d in details:
                        log.line(f"    {d}")
                if not stopped:
                    control("AFTER")
    finally:
        left = sidecars(repo)
        if left:
            log.line("SIDECARS LEFT: " + "; ".join(os.path.relpath(s, repo) for s in left))
        log.close()

    counts, controls, was_stopped, missing = read_log(log.path, names)
    code = exit_code_from_log(counts, controls, was_stopped)
    with open(log.path, "a", encoding="utf-8", newline="\n") as f:
        def say(text):
            f.write(text + "\n")
            print(text, flush=True)
        say("")
        say("SUMMARY, read back from this log: " +
            ", ".join(f"{v} {k.lower()}" for k, v in counts.items()) + f", of {len(names)}")
        say(f"CONTROLS: before {controls.get('BEFORE', 'not run').lower()}, "
            f"after {controls.get('AFTER', 'not run').lower()}")
        for n in missing:
            say(f"NOT RUN: {n}")
        say(f"EXIT {code}")
    print(f"The log and each build's and test run's output: {folder}")
    print("On a machine short of memory, `dotnet build-server shutdown` frees the compiler server.")
    return code


def check_lists(repo, paths):
    """Read-only: every list's shape, and every anchor exactly once on today's
    tree. What CI runs on every push."""
    bad = 0
    for path in paths:
        try:
            lst = load_list(path)
        except ListError as e:
            print(f"BAD LIST {e}")
            bad += 1
            continue
        problems = check_anchors(repo, lst)
        for p in problems:
            print(f"BAD ANCHOR {path}: {p}")
        bad += bool(problems)
        print(f"{'ok ' if not problems else 'BAD'} {path}: {len(lst['mutants'])} mutant(s), "
              f"{len(problems)} problem(s)")
    print(f"{len(paths) - bad} of {len(paths)} list(s) anchor on this tree")
    return EXIT_OK if paths and not bad else EXIT_BAD_LIST


def restore(repo):
    left = sidecars(repo)
    for s in left:
        print("restored", os.path.relpath(put_back(s), repo))
    print(f"{len(left)} file(s) put back; check `git status`")
    return EXIT_OK


# -------------------------------------------------------------- self-test

def self_test():
    """The runner's own rules, each shown to hold and each able to fail, on
    canned output and a scratch folder. Builds nothing."""
    results = []

    def check(what, ok):
        results.append(ok)
        print(f"{'pass' if ok else 'FAIL'}  {what}")

    b_ok = "Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n"
    b_bad = "x.cs(1,1): error CS0103: The name 'y' does not exist [p.csproj]\nBuild FAILED.\n"
    t_fail = ("[xUnit.net 00:00:00.66]     Sample.Tests.T.A_theory(value: \"a.b.c\") [FAIL]\n"
              "  Failed Sample.Tests.T.A_theory(value: \"a.b.c\") [2 ms]\n"
              "  Failed Sample.Tests.T+Nested.A_plain_test [112 ms]\n"
              "  Error Message:\n   Assert.True() Failure\n\n"
              "Failed!  - Failed:     2, Passed:     1, Skipped:     0, Total:     3, "
              "Duration: 1 s - Sample.Tests.dll (net10.0)\n")
    t_pass = ("Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, "
              "Duration: 1 s - Sample.Tests.dll (net10.0)\n")
    t_crash = ("The active test run was aborted. Reason: Test host process crashed : Stack overflow.\n\n"
               "Test Run Aborted.\n")
    t_none = "No test matches the given testcase filter `FullyQualifiedName~Nothing` in x.dll\n"

    check("a theory's name is cut at '(' before its dotted argument",
          failed_tests(t_fail) == ["Sample.Tests.T+Nested.A_plain_test", "Sample.Tests.T.A_theory"])
    check("a named failure after a clean build is CAUGHT",
          judge(0, b_ok, 1, t_fail, False, False)[0] == "CAUGHT")
    check("a failed build is NO VERDICT, whatever the tests say",
          judge(1, b_bad, 1, t_fail, False, False)[0] == "NO VERDICT")
    check("exit 0 without 'Build succeeded' is NO VERDICT",
          judge(0, "", 1, t_fail, False, False)[0] == "NO VERDICT")
    check("every test passing is SURVIVED", judge(0, b_ok, 0, t_pass, False, False)[0] == "SURVIVED")
    check("a filter that reaches no test (which exits 0) is NO VERDICT, not SURVIVED",
          judge(0, b_ok, 0, t_none, False, False)[0] == "NO VERDICT")
    check("a crash is CAUGHT only for a mutant that expects one",
          judge(0, b_ok, 1, t_crash, False, True)[0] == "CAUGHT"
          and judge(0, b_ok, 1, t_crash, False, False)[0] == "NO VERDICT")
    check("a run stopped at its time limit is NO VERDICT",
          judge(0, b_ok, 1, t_fail, True, False)[0] == "NO VERDICT")
    unnamed = t_fail.replace("  Failed Sample", "  Sample").replace(" [FAIL]", "")
    check("a failure count with no failed test named is NO VERDICT",
          judge(0, b_ok, 1, unnamed, False, False)[0] == "NO VERDICT")
    check("a control passes on a clean pass and fails on a failure, a crash or no match",
          control_passed(0, b_ok, 0, t_pass, False)[0]
          and not control_passed(0, b_ok, 1, t_fail, False)[0]
          and not control_passed(0, b_ok, 1, t_crash, False)[0]
          and not control_passed(0, b_ok, 0, t_none, False)[0])

    with tempfile.TemporaryDirectory() as repo:
        os.makedirs(os.path.join(repo, "src"))
        lf = os.path.join(repo, "src", "a.cs")
        crlf = os.path.normpath(os.path.join(repo, "src", "b.cs"))
        mixed = os.path.join(repo, "src", "c.cs")
        with open(lf, "wb") as f:
            f.write(b"if (x)\n    return 1;\nreturn 2;\nreturn 2;\n")
        with open(crlf, "wb") as f:
            f.write("\ufeffif (x)\r\n    return 1;\r\n".encode("utf-8"))
        with open(mixed, "wb") as f:
            f.write(b"if (x)\r\n    return 1;\n")
        os.makedirs(os.path.join(repo, "tests", "P"))
        open(os.path.join(repo, "tests", "P", "P.csproj"), "w").close()

        def lst(*muts, **top):
            base = {"name": "t", "commit": "0000000", "project": "tests/P/P.csproj", "mutants": list(muts)}
            base.update(top)
            return base

        one = {"name": "m", "file": "src/a.cs", "old": "if (x)\n    return 1;",
               "new": "if (!x)\n    return 1;", "filter": "FullyQualifiedName~A"}
        check("an anchor that occurs once passes the check", check_anchors(repo, lst(one)) == [])
        check("an anchor that occurs twice is refused",
              len(check_anchors(repo, lst(dict(one, old="return 2;")))) == 1)
        check("an anchor that occurs nowhere is refused",
              len(check_anchors(repo, lst(dict(one, old="return 3;")))) == 1)
        check("a missing file is refused", len(check_anchors(repo, lst(dict(one, file="src/z.cs")))) == 1)
        check("an anchor written with \\n matches a CRLF file",
              check_anchors(repo, lst(dict(one, file="src/b.cs"))) == [])
        check("a file with mixed line endings is refused",
              len(check_anchors(repo, lst(dict(one, file="src/c.cs")))) == 1)

        list_path = os.path.join(repo, "l.json")

        def refused(data):
            with open(list_path, "w", encoding="utf-8") as f:
                json.dump(data, f)
            try:
                load_list(list_path)
                return False
            except ListError:
                return True

        check("a well-formed list loads", not refused(lst(one)))
        check("another runner's keys ('find', 'replace') are refused",
              refused(lst({"name": "m", "file": "src/a.cs", "find": "x", "replace": "y", "filter": "F"})))
        check("a mutant with no filter is refused",
              refused(lst({k: v for k, v in one.items() if k != "filter"})))
        check("a name used twice is refused", refused(lst(one, dict(one))))
        check("a path out of the repository is refused", refused(lst(dict(one, file="../x.cs"))))
        check("a platform other than linux or windows is refused", refused(lst(one, platform="mac")))
        check("expect other than 'crash' is refused", refused(lst(dict(one, expect="fail"))))
        check("a list with no commit is refused", refused({"name": "t", "mutants": [one]}))

        before = read_bytes(crlf)
        written = mutate(repo, dict(one, file="src/b.cs"))
        mutated = read_bytes(crlf)
        check("a mutant writes its sidecar before the file changes, and keeps CRLF and the BOM",
              written == [crlf + BACKUP] and read_bytes(crlf + BACKUP) == before
              and mutated.startswith("\ufeff".encode("utf-8")) and b"if (!x)\r\n" in mutated)
        check("a sidecar in the tree is found", sidecars(repo) == [crlf + BACKUP])
        out_dir = os.path.join(repo, "out")
        with open(list_path, "w", encoding="utf-8") as f:
            json.dump(lst(one), f)
        check("a run refuses to start while a sidecar exists",
              run_list(repo, list_path, [], 0, out_dir, 60) == EXIT_SIDECAR)
        for s in written:
            put_back(s)
        check("the restore puts the bytes back and removes the sidecar",
              read_bytes(crlf) == before and sidecars(repo) == [])

        log_path = os.path.join(repo, "run.log")
        with open(log_path, "w", encoding="utf-8") as f:
            f.write("CONTROL BEFORE: PASSED 4 passed, 0 failed\n"
                    "VERDICT CAUGHT: a: b\n    failed: T.X\n"
                    "VERDICT SURVIVED: second\n"
                    "VERDICT SKIPPED: third\n"
                    "CONTROL AFTER: PASSED 4 passed, 0 failed\n")
        counts, controls, stopped, missing = read_log(log_path, ["a: b", "second", "third", "fourth"])
        check("the summary is read from the log: a name holding ': ' is matched whole, "
              "and a mutant with no line is NOT RUN",
              counts == {"CAUGHT": 1, "SURVIVED": 1, "NO VERDICT": 0, "SKIPPED": 1, "NOT RUN": 1}
              and missing == ["fourth"])
        check("a survivor makes the exit code 1",
              exit_code_from_log(counts, controls, stopped) == EXIT_NOT_ALL_CAUGHT)
        all_caught = {"CAUGHT": 2, "SURVIVED": 0, "NO VERDICT": 0, "SKIPPED": 1, "NOT RUN": 0}
        check("every mutant that ran caught, a skip aside, between two passing controls, is exit 0",
              exit_code_from_log(all_caught, controls, False) == EXIT_OK)
        check("the same with the control after failed is exit 1",
              exit_code_from_log(all_caught, {"BEFORE": "PASSED", "AFTER": "FAILED"}, False)
              == EXIT_NOT_ALL_CAUGHT)
        check("a run where nothing ran on this machine is not exit 0",
              exit_code_from_log({"CAUGHT": 0, "SURVIVED": 0, "NO VERDICT": 0, "SKIPPED": 2, "NOT RUN": 0},
                                 {}, False) == EXIT_NOTHING_RAN)
        check("a run stopped for memory is exit 4",
              exit_code_from_log(all_caught, controls, True) == EXIT_LOW_MEMORY)

        other = "windows" if this_platform() == "linux" else "linux"
        with open(list_path, "w", encoding="utf-8") as f:
            json.dump(lst(one, platform=other), f)
        code = run_list(repo, list_path, [], 0, out_dir, 60)
        logs = sorted(glob.glob(os.path.join(out_dir, "*", "run.log")))
        text = open(logs[-1], encoding="utf-8").read() if logs else ""
        check("a mutant for another platform is reported skipped by name and never counted caught",
              code == EXIT_NOTHING_RAN and "VERDICT SKIPPED: m\n" in text and "VERDICT CAUGHT" not in text)

    # A parent that exits at once and leaves a child of its own holding the
    # output open for 60 s: the child is outside any tree the kill can name,
    # so a runner that waits for the output after the kill waits the 60 s.
    holder = [sys.executable, "-c",
              "import subprocess, sys; "
              "subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'], stdout=sys.stdout, stderr=sys.stdout); "
              "print('left a child holding the output', flush=True)"]
    started = time.monotonic()
    _, _, timed_out = run_process(holder, HERE, 2)
    took = time.monotonic() - started
    check(f"a run whose output a process outside its tree holds open ends at its limit and the grace ({took:.0f} s), "
          f"never waits for that process",
          timed_out and took < 2 + 2 * KILL_GRACE_S + 5)

    failed = results.count(False)
    print(f"{len(results) - failed} of {len(results)} self-test checks pass")
    return EXIT_OK if not failed else EXIT_NOT_ALL_CAUGHT


# ------------------------------------------------------------------ main

def main(argv=None):
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except AttributeError:
        pass
    ap = argparse.ArgumentParser(description="Break the code on purpose and watch a test fail.")
    ap.add_argument("lists", nargs="*", help="a list to run, or with --check any number of lists")
    ap.add_argument("--check", action="store_true", help="read-only: check each list's shape and anchors")
    ap.add_argument("--restore", action="store_true", help="put back every file a stopped run left")
    ap.add_argument("--self-test", action="store_true", help="check the runner's own rules; builds nothing")
    ap.add_argument("--only", nargs="+", default=[], metavar="NAME", help="run only these mutants, by name")
    ap.add_argument("--min-free-gb", type=float, default=4.0,
                    help="stop between mutants when free memory falls under this (default 4; 0 turns it off)")
    ap.add_argument("--repo", default=DEFAULT_REPO, help="the repository root (default: two folders up)")
    ap.add_argument("--out", default=None, help="where run folders go (default tests/mutants/runs)")
    ap.add_argument("--timeout-minutes", type=float, default=30,
                    help="the limit for each build and each test run (default 30)")
    args = ap.parse_args(argv)
    repo = os.path.abspath(args.repo)

    if args.self_test:
        return self_test()
    if args.restore:
        return restore(repo)
    if args.check:
        return check_lists(repo, args.lists)
    if len(args.lists) != 1:
        ap.error("give exactly one list to run")
    return run_list(repo, args.lists[0], args.only, args.min_free_gb, args.out, args.timeout_minutes * 60)


if __name__ == "__main__":
    sys.exit(main())
