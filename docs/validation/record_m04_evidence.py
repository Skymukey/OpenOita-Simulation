"""汇总M04实际Unity XML结果，记录专属文件哈希并核对meta/GUID；不删除文件。"""
import hashlib
import json
import re
import xml.etree.ElementTree as ET
from datetime import datetime, timedelta, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "docs/validation"
xml_path = OUTPUT / "m04-editmode-results.xml"
tree = ET.parse(xml_path).getroot()
tests = []
for item in tree.iter("test-case"):
    tests.append({
        "fullName": item.get("fullname"), "name": item.get("name"), "state": item.get("result"),
        "durationSeconds": float(item.get("duration", 0)), "output": item.findtext("output", ""),
        "message": item.findtext("failure/message", item.findtext("reason/message", "")),
        "stackTrace": item.findtext("failure/stack-trace", ""),
    })
module_tests = [item for item in tests if ".Structure.Connectivity." in item["fullName"]]
assert len(module_tests) == 38
assert all(item["state"] == "Passed" for item in module_tests)
report = {
    "模块": "M04", "证据来源": "Unity Test Runner原始XML；JSON仅汇总，不替代XML",
    "原始XML": str(xml_path), "XML_SHA256": hashlib.sha256(xml_path.read_bytes()).hexdigest(),
    "开始时间UTC": tree.get("start-time"), "结束时间UTC": tree.get("end-time"),
    "全项目": {key: tree.get(key) for key in ("result", "total", "passed", "failed", "skipped", "duration")},
    "本模块": {"总数": len(module_tests), "通过": len(module_tests), "失败": 0, "未测": 0},
    "逐项": tests,
}
(OUTPUT / "m04-editmode-result.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

test_folder = ROOT / "Assets/Tests/OpenOita/EditMode/Structure/Connectivity"
assets = [path for path in test_folder.rglob("*") if path.is_file()]
for name in ("ConnectivityAnalyzer.cs", "FixedCellPolicy.cs"):
    source = ROOT / "Assets/Scripts/OpenOitaWorld/Structure" / name
    assets += [source, Path(str(source) + ".meta")]
assets += [ROOT / "Assets/Scripts/OpenOitaWorld/Structure.meta", ROOT / "Assets/Tests/OpenOita/EditMode/Structure.meta",
           ROOT / "Assets/Tests/OpenOita/EditMode/Structure/Connectivity.meta"]
for path in assets:
    if path.suffix != ".meta":
        assert Path(str(path) + ".meta").is_file(), f"缺少Unity元数据：{path}"
guids = {}
for path in (ROOT / "Assets").rglob("*.meta"):
    match = re.search(r"^guid: ([a-f0-9]{32})$", path.read_text(encoding="utf-8-sig"), re.M)
    if match:
        guids.setdefault(match[1], []).append(path)
for path in assets:
    if path.suffix != ".meta":
        continue
    match = re.search(r"^guid: ([a-f0-9]{32})$", path.read_text(encoding="utf-8-sig"), re.M)
    assert match and len(guids[match[1]]) == 1, f"无效或重复GUID：{path}"
documents = [ROOT / "docs/specs/M04-结构连通与固定.md", OUTPUT / "M04-执行结果与交接.md",
             OUTPUT / "M04-公共接口差异与集成清单.md", OUTPUT / "record_m04_evidence.py",
             xml_path, OUTPUT / "m04-editmode-result.json", OUTPUT / "m04-test-job.json", OUTPUT / "m04-environment.json"]
files = sorted(set(assets + documents))
assert all(path.is_file() for path in files)
config_paths = [ROOT / "docs/examples" / name for name in ("materials.json", "world_config.json", "scene.json")]
inventory = {
    "模块": "M04", "合同": "M00-2/CONTRACTS1.1/主方案0.9",
    "记录时间": datetime.now(timezone(timedelta(hours=8))).isoformat(),
    "元数据结果": "专属新增资产均有Unity生成meta，GUID无冲突",
    "配置": [{"路径": str(path), "SHA256": hashlib.sha256(path.read_bytes()).hexdigest()} for path in config_paths],
    "文件": [{"路径": str(path), "相对路径": path.relative_to(ROOT).as_posix(), "字节": path.stat().st_size,
             "SHA256": hashlib.sha256(path.read_bytes()).hexdigest()} for path in files],
}
(OUTPUT / "m04-file-inventory.json").write_text(json.dumps(inventory, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(json.dumps({"M04测试": report["本模块"], "全项目": report["全项目"], "文件数": len(files),
                  "资产数": len(assets), "元数据结果": inventory["元数据结果"]}, ensure_ascii=False))
