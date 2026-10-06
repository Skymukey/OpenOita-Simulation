"""核对M05回补范围、元数据与实际测试报告，不删除文件。"""
import hashlib
import json
import re
import subprocess
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "docs/validation"
PREFIX = "m05-backfill"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save(name, data):
    (OUTPUT / name).write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


baseline = json.loads((OUTPUT / f"{PREFIX}-start-baseline.json").read_text(encoding="utf-8"))
protected = []
for relative, before in baseline["protected"].items():
    if relative == "docs/specs/M05-材料体与几何事务.md":
        continue
    path = ROOT / relative
    after = sha(path)
    assert before == after, f"受保护文件改变：{path}"
    protected.append({"路径": str(path), "SHA256": after, "前后相同": True})

source = ROOT / "Assets/Tests/OpenOita/EditMode/Bodies/M05StateTransactionAcceptanceTests.cs"
meta = Path(str(source) + ".meta")
assert meta.is_file(), "新增测试缺少Unity元数据"
guid = re.search(r"^guid: ([a-f0-9]{32})$", meta.read_text(encoding="utf-8-sig"), re.M)
assert guid, "新增测试GUID无效"
matches = []
for path in (ROOT / "Assets").rglob("*.meta"):
    if re.search(r"^guid: " + guid[1] + r"$", path.read_text(encoding="utf-8-sig"), re.M):
        matches.append(str(path))
assert matches == [str(meta)], "新增测试GUID存在重复"
for number, line in enumerate(source.read_text(encoding="utf-8").splitlines(), 1):
    assert line == line.rstrip(), f"新增测试存在行尾空白：{number}"
check = subprocess.run(["git", "diff", "--check"], cwd=ROOT, capture_output=True, text=True, encoding="utf-8")
assert check.returncode == 0, check.stdout + check.stderr

result = {"模块": "M05", "合同": "CONTRACTS1.4/M00-5/主方案0.9", "本轮新增用例": 13,
          "本轮测试状态": "未测", "原因": "Unity桥接启动超时及断连，未取得本轮实际测试报告。"}
xml_path = OUTPUT / f"{PREFIX}-editmode-results.xml"
if xml_path.is_file():
    tree = ET.parse(xml_path).getroot()
    tests = [{"完整名称": item.get("fullname"), "状态": item.get("result"),
              "时长秒": float(item.get("duration", 0)), "输出": item.findtext("output", ""),
              "诊断": item.findtext("failure/message", item.findtext("reason/message", "")),
              "堆栈": item.findtext("failure/stack-trace", "")} for item in tree.iter("test-case")]
    module = [test for test in tests if ".M05StateTransactionAcceptanceTests." in test["完整名称"]]
    assert len(module) == 13, "报告不包含全部13项本轮回补用例，不能沿用旧报告"
    start = datetime.strptime(tree.get("start-time"), "%Y-%m-%d %H:%M:%SZ").replace(tzinfo=timezone.utc).timestamp()
    assert source.stat().st_mtime < start, "源码晚于测试开始，须重新运行"
    job = json.loads((OUTPUT / f"{PREFIX}-test-job.json").read_text(encoding="utf-8"))
    data = job["structuredContent"]["result"]["data"]
    assert data["status"] == "succeeded", "MCP作业尚未成功结束"
    assert data["result"]["summary"]["total"] == len(tests), "MCP与XML用例总数不一致"
    states = {test["fullName"]: test["state"].split(":")[0] for test in data["result"]["results"]}
    assert all(states[test["完整名称"]] == test["状态"].split(":")[0] for test in tests), "MCP与XML逐项状态不一致"
    binaries = [ROOT / "Library/ScriptAssemblies" / name for name in ("OpenOita.Runtime.dll", "OpenOita.Tests.EditMode.dll")]
    assert all(path.is_file() and path.stat().st_mtime < start for path in binaries), "编译程序集晚于测试开始或缺失"
    result.update({"本轮测试状态": "通过" if all(test["状态"] == "Passed" for test in module) else "存在未通过项",
                   "原因": "依据本轮Unity Test Runner原始XML。", "XML_SHA256": sha(xml_path),
                   "开始时间UTC": tree.get("start-time"), "结束时间UTC": tree.get("end-time"),
                   "MCP作业": data["job_id"], "MCP与XML逐项一致": True,
                   "已编译程序集": [{"路径": str(path), "SHA256": sha(path), "字节": path.stat().st_size} for path in binaries],
                   "完整程序集": {key: tree.get(key) for key in ("result", "total", "passed", "failed", "skipped", "duration")},
                   "本轮逐项": module, "全部逐项": tests})
save(f"{PREFIX}-editmode-result.json", result)

documents = [ROOT / "docs/specs/M05-材料体与几何事务.md", OUTPUT / "M05-回补结果与交接.md",
             OUTPUT / "M05-执行结果与交接.md", OUTPUT / "M05-公共接口差异与集成清单.md", Path(__file__)]
documents += sorted(path for path in OUTPUT.glob(f"{PREFIX}-*") if path.name != f"{PREFIX}-file-inventory.json")
assets = [source, meta]
inventory = {"模块": "M05", "记录时间UTC": datetime.now(timezone.utc).isoformat(),
             "开工保护文件数": len(baseline["protected"]), "保持相同文件数": len(protected), "受保护文件": protected,
             "新资产GUID唯一": True, "新资产路径": [str(path) for path in assets],
             "git_diff_check": {"退出码": check.returncode, "输出": check.stdout, "提示": check.stderr},
             "文件": [{"路径": str(path), "SHA256": sha(path), "字节": path.stat().st_size} for path in sorted(set(assets + documents))]}
save(f"{PREFIX}-file-inventory.json", inventory)
print(json.dumps({"保护文件": len(protected), "新资产GUID唯一": True, "测试状态": result["本轮测试状态"],
                  "完整程序集": result.get("完整程序集"), "本轮用例": len(result.get("本轮逐项", []))}, ensure_ascii=False))
