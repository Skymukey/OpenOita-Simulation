"""核对本轮M05原始测试、源码、保护基线和Unity元数据；不删除文件。"""
import hashlib
import json
import re
import subprocess
import xml.etree.ElementTree as ET
from datetime import datetime, timedelta, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "docs/validation"
PREFIX = "m05"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save(name, value):
    (OUTPUT / name).write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


xml_path = OUTPUT / "m05-editmode-results.xml"
tree = ET.parse(xml_path).getroot()
results = [{
    "fullName": item.get("fullname"), "name": item.get("name"), "state": item.get("result"),
    "durationSeconds": float(item.get("duration", 0)), "output": item.findtext("output", ""),
    "message": item.findtext("failure/message", item.findtext("reason/message", "")),
    "stackTrace": item.findtext("failure/stack-trace", ""),
} for item in tree.iter("test-case")]
module = [item for item in results if ".Bodies." in item["fullName"] or ".Structure.Geometry." in item["fullName"]]
assert len(module) == 53 and all(item["state"] == "Passed" for item in module)
assert int(tree.get("total")) == 329 and int(tree.get("passed")) == 328 and int(tree.get("failed")) == 0
job = json.loads((OUTPUT / "m05-test-job.json").read_text(encoding="utf-8"))
data = job["structuredContent"]["result"]["data"]
assert data["job_id"] == "20dff1b3cff0450fa7501af86d085c50" and data["status"] == "succeeded"
assert data["result"]["summary"]["total"] == len(results)
job_states = {item["fullName"]: item["state"] for item in data["result"]["results"]}
assert all(job_states[item["fullName"]].split(":")[0] == item["state"].split(":")[0] for item in results)
summary = {
    "模块": "M05", "合同": "M00-3/CONTRACTS1.2/主方案0.9", "时区": "Asia/Shanghai",
    "证据来源": "Unity Test Runner原始XML，并与同次MCP完整逐项结果核对",
    "XML原始路径": "C:/Users/LYH/AppData/LocalLow/DefaultCompany/OpenOita/TestResults.xml",
    "XML_SHA256": sha(xml_path), "MCP作业": data["job_id"],
    "开始时间UTC": tree.get("start-time"), "结束时间UTC": tree.get("end-time"),
    "全项目": {key: tree.get(key) for key in ("result", "total", "passed", "failed", "skipped", "duration")},
    "本模块": {"总数": len(module), "通过": len(module), "失败": 0, "忽略": 0}, "逐项": results,
}
save("m05-editmode-result.json", summary)

baseline = json.loads((OUTPUT / "m05-start-baseline.json").read_text(encoding="utf-8"))
protected = []
for relative, before in baseline["protected"].items():
    if relative == "docs/specs/M05-材料体与几何事务.md":
        continue
    path = ROOT / relative
    after = sha(path)
    assert before == after, f"受保护文件改变：{path}"
    protected.append({"路径": str(path), "前后SHA256相同": True, "SHA256": after})

source_paths = [ROOT / "Assets/Scripts/OpenOitaWorld/Structure" / name for name in (
    "BodyExtractionPlanner.cs", "BodyExtractionPlan.cs", "RectangleGeometryBuilder.cs", "MassPropertiesCalculator.cs",
)]
source_paths += [ROOT / "Assets/Scripts/OpenOitaWorld/Simulation/Bodies/MaterialBody.cs"]
source_paths += sorted((ROOT / "Assets/Tests/OpenOita/EditMode/Bodies").glob("*.cs"))
source_paths += sorted((ROOT / "Assets/Tests/OpenOita/EditMode/Structure/Geometry").glob("*.cs"))
assets = source_paths + [Path(str(path) + ".meta") for path in source_paths]
assets += [ROOT / relative for relative in (
    "Assets/Scripts/OpenOitaWorld/Simulation/Bodies.meta", "Assets/Tests/OpenOita/EditMode/Bodies.meta",
    "Assets/Tests/OpenOita/EditMode/Structure/Geometry.meta",
)]
guid_index = {}
for path in (ROOT / "Assets").rglob("*.meta"):
    match = re.search(r"^guid: ([a-f0-9]{32})$", path.read_text(encoding="utf-8-sig"), re.M)
    if match:
        guid_index.setdefault(match[1], []).append(path)
for path in assets:
    assert path.is_file(), f"缺少源码或元数据：{path}"
    if path.suffix == ".meta":
        match = re.search(r"^guid: ([a-f0-9]{32})$", path.read_text(encoding="utf-8-sig"), re.M)
        assert match and len(guid_index[match[1]]) == 1, f"GUID无效或重复：{path}"
start = datetime.strptime(tree.get("start-time"), "%Y-%m-%d %H:%M:%SZ").replace(tzinfo=timezone.utc).timestamp()
assert all(path.stat().st_mtime < start for path in source_paths), "存在晚于测试开始修改的源码，需重新测试"
binaries = [ROOT / "Library/ScriptAssemblies" / name for name in ("OpenOita.Runtime.dll", "OpenOita.Tests.EditMode.dll")]
assert all(path.is_file() and path.stat().st_mtime < start for path in binaries), "编译程序集晚于测试开始或缺失"
check = subprocess.run(["git", "diff", "--check"], cwd=ROOT, text=True, encoding="utf-8", capture_output=True)
assert check.returncode == 0, check.stdout + check.stderr

documents = [ROOT / "docs/specs/M05-材料体与几何事务.md", OUTPUT / "M05-执行结果与交接.md",
             OUTPUT / "M05-公共接口差异与集成清单.md", OUTPUT / "record_m05_evidence.py", xml_path]
documents += sorted(path for path in OUTPUT.glob("m05-*.json") if path.name != "m05-file-inventory.json")
files = sorted(set(assets + documents))
inventory = {
    "模块": "M05", "合同": "M00-3/CONTRACTS1.2/主方案0.9",
    "记录时间": datetime.now(timezone(timedelta(hours=8))).isoformat(),
    "开工受保护文件总数": len(baseline["protected"]), "受保护文件": protected,
    "元数据结果": "新增M05资产meta齐全且GUID全Assets唯一；既有GUID未改写",
    "源码时间检查": "全部M05源码及编译程序集早于本轮测试开始",
    "已编译程序集": [{"路径": str(path), "SHA256": sha(path), "字节": path.stat().st_size} for path in binaries],
    "git_diff_check": {"退出码": check.returncode, "输出": check.stdout, "提示": check.stderr},
    "配置": [{"路径": str(path), "SHA256": sha(path)} for path in (ROOT / "docs/examples" / name for name in (
        "materials.json", "world_config.json", "scene.json",
    ))],
    "文件": [{"路径": str(path), "相对路径": path.relative_to(ROOT).as_posix(), "字节": path.stat().st_size,
              "SHA256": sha(path)} for path in files],
}
save("m05-file-inventory.json", inventory)
print(json.dumps({"模块": summary["本模块"], "全项目": summary["全项目"], "受保护文件未改变": len(protected),
                  "交付文件数": len(files), "元数据": inventory["元数据结果"]}, ensure_ascii=False))
