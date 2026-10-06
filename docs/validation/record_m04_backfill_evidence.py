"""记录M04回补的Unity原始结果和当前文件哈希，验证受保护文件与meta；不删除文件。"""
import hashlib
import json
import re
import xml.etree.ElementTree as ET
from datetime import datetime, timedelta, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "docs/validation"
PREFIX = "m04-backfill"
xml_path = OUTPUT / f"{PREFIX}-editmode-results.xml"
tree = ET.parse(xml_path).getroot()
results = [{
    "fullName": item.get("fullname"), "name": item.get("name"), "state": item.get("result"),
    "durationSeconds": float(item.get("duration", 0)), "output": item.findtext("output", ""),
    "message": item.findtext("failure/message", item.findtext("reason/message", "")),
    "stackTrace": item.findtext("failure/stack-trace", ""),
} for item in tree.iter("test-case")]
module = [item for item in results if ".Structure.Connectivity." in item["fullName"]]
assert len(module) == 40 and all(item["state"] == "Passed" for item in module)
assert int(tree.get("failed")) == 0
summary = {
    "模块": "M04回补", "合同": "M00-3/CONTRACTS1.2/主方案0.9", "时区": "Asia/Shanghai",
    "证据来源": "Unity Test Runner原始XML；JSON仅提取，不替代XML",
    "XML_SHA256": hashlib.sha256(xml_path.read_bytes()).hexdigest(),
    "开始时间UTC": tree.get("start-time"), "结束时间UTC": tree.get("end-time"),
    "全项目": {key: tree.get(key) for key in ("result", "total", "passed", "failed", "skipped", "duration")},
    "本模块": {"总数": 40, "通过": 40, "失败": 0, "未测": 0}, "逐项": results,
}
(OUTPUT / f"{PREFIX}-editmode-result.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

baseline = json.loads((OUTPUT / f"{PREFIX}-baseline.json").read_text(encoding="utf-8"))
modified_sources = {
    "Assets/Scripts/OpenOitaWorld/Structure/ConnectivityAnalyzer.cs",
    "Assets/Tests/OpenOita/EditMode/Structure/Connectivity/ConnectivityAnalyzerTests.cs",
}
protected = []
for entry in baseline["files"]:
    path = Path(entry["Path"])
    if path.relative_to(ROOT).as_posix() in modified_sources:
        continue
    after = hashlib.sha256(path.read_bytes()).hexdigest()
    assert after.lower() == entry["Hash"].lower(), f"受保护文件在本轮改变：{path}"
    protected.append({"路径": str(path), "前后SHA256相同": True, "SHA256": after})

assets = []
for relative in (
    "Assets/Scripts/OpenOitaWorld/Structure/ConnectivityAnalyzer.cs",
    "Assets/Scripts/OpenOitaWorld/Structure/FixedCellPolicy.cs",
    "Assets/Tests/OpenOita/EditMode/Structure/Connectivity/ConnectivityFixture.cs",
    "Assets/Tests/OpenOita/EditMode/Structure/Connectivity/ConnectivityAnalyzerTests.cs",
    "Assets/Tests/OpenOita/EditMode/Structure/Connectivity/ConnectivityStateCoreTests.cs",
):
    path = ROOT / relative
    assets += [path, Path(str(path) + ".meta")]
assets += [ROOT / "Assets/Scripts/OpenOitaWorld/Structure.meta", ROOT / "Assets/Tests/OpenOita/EditMode/Structure.meta",
           ROOT / "Assets/Tests/OpenOita/EditMode/Structure/Connectivity.meta"]
guid_index = {}
for path in (ROOT / "Assets").rglob("*.meta"):
    match = re.search(r"^guid: ([a-f0-9]{32})$", path.read_text(encoding="utf-8-sig"), re.M)
    if match:
        guid_index.setdefault(match[1], []).append(path)
for path in assets:
    assert path.is_file(), f"缺少源码或Unity元数据：{path}"
    if path.suffix == ".meta":
        match = re.search(r"^guid: ([a-f0-9]{32})$", path.read_text(encoding="utf-8-sig"), re.M)
        assert match and len(guid_index[match[1]]) == 1, f"无效或重复GUID：{path}"
documents = [ROOT / "docs/specs/M04-结构连通与固定.md", OUTPUT / "M04-执行结果与交接.md",
             OUTPUT / "M04-公共接口差异与集成清单.md", OUTPUT / "M04-回补结果与交接.md",
             OUTPUT / "record_m04_backfill_evidence.py", xml_path]
documents += [OUTPUT / f"{PREFIX}-{name}.json" for name in ("baseline", "environment", "test-job", "editmode-result")]
files = sorted(set(assets + documents))
inventory = {
    "模块": "M04回补", "合同": "M00-3/CONTRACTS1.2/主方案0.9",
    "记录时间": datetime.now(timezone(timedelta(hours=8))).isoformat(),
    "受保护文件": protected, "元数据结果": "源码meta齐全且GUID全Assets唯一，没有新增或更换资产GUID",
    "配置": [{"路径": str(path), "SHA256": hashlib.sha256(path.read_bytes()).hexdigest()}
             for path in (ROOT / "docs/examples" / name for name in ("materials.json", "world_config.json", "scene.json"))],
    "文件": [{"路径": str(path), "相对路径": path.relative_to(ROOT).as_posix(), "字节": path.stat().st_size,
             "SHA256": hashlib.sha256(path.read_bytes()).hexdigest()} for path in files],
}
(OUTPUT / f"{PREFIX}-file-inventory.json").write_text(json.dumps(inventory, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(json.dumps({"模块": summary["本模块"], "全项目": summary["全项目"], "受保护文件未改变": len(protected),
                  "文件数": len(files), "元数据": inventory["元数据结果"]}, ensure_ascii=False))
