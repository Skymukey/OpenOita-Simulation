"""记录M00交付源码/资产哈希并核对Unity自动生成的元数据，不生成GUID。"""
import hashlib
import json
import re
from datetime import datetime, timedelta, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
roots = [ROOT / "Assets/Scripts/OpenOitaWorld/Core/Contracts", ROOT / "Assets/Tests"]
files = [path for folder in roots for path in folder.rglob("*") if path.is_file()]
files += [ROOT / "Assets/Scripts/OpenOitaWorld/Core/Contracts.meta", ROOT / "Assets/Tests.meta",
          ROOT / "Assets/Scripts/OpenOitaWorld/OpenOita.Runtime.asmdef", ROOT / "Assets/Scripts/OpenOitaWorld/OpenOita.Runtime.asmdef.meta"]
for path in files:
    if path.suffix != ".meta":
        assert Path(str(path) + ".meta").is_file(), f"缺少Unity元数据：{path}"
guid_paths = {}
for path in (ROOT / "Assets").rglob("*.meta"):
    match = re.search(r"^guid: ([a-f0-9]{32})$", path.read_text(encoding="utf-8-sig"), re.M)
    if match:
        guid_paths.setdefault(match[1], []).append(str(path.relative_to(ROOT)))
new_guids = []
for path in files:
    if path.suffix == ".meta":
        match = re.search(r"^guid: ([a-f0-9]{32})$", path.read_text(encoding="utf-8-sig"), re.M)
        assert match, f"无效Unity元数据：{path}"
        assert len(guid_paths[match[1]]) == 1, f"GUID重复：{path}"
        new_guids.append(match[1])
assert len(new_guids) == len(set(new_guids))
report = {
    "合同版本": "M00-1", "记录时间": datetime.now(timezone(timedelta(hours=8))).isoformat(),
    "元数据结果": "全部新增资产有Unity生成meta且GUID无冲突",
    "文件": [{"路径": str(path.relative_to(ROOT)).replace("\\", "/"), "SHA256": hashlib.sha256(path.read_bytes()).hexdigest()}
             for path in sorted(set(files))],
}
output = ROOT / "docs/validation/m00-file-inventory.json"
output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(json.dumps({"文件数": len(report["文件"]), "元数据数": len(new_guids), "结果": report["元数据结果"]}, ensure_ascii=False))
