"""M00 工具校验：严格 JSON、三份 Schema、跨文件语义；不代表 Unity 加载器验收。"""
import hashlib
import importlib.metadata
import json
from pathlib import Path
from collections import Counter
from copy import deepcopy
import jsonschema

ROOT = Path(__file__).resolve().parents[2]


def canonical(value):
    # JSON相等：对象键顺序无关，1与1.0相等，但true与1不同；避免大对象数组逐对比较。
    if value is None:
        return ("null",)
    if isinstance(value, bool):
        return ("bool", value)
    if isinstance(value, (int, float)):
        return ("number", value)
    if isinstance(value, str):
        return ("string", value)
    if isinstance(value, list):
        return ("array", tuple(canonical(item) for item in value))
    return ("object", tuple((key, canonical(item)) for key, item in sorted(value.items())))


def unique_items(validator, enabled, instance, schema):
    if enabled and isinstance(instance, list):
        keys = [canonical(item) for item in instance]
        if len(keys) != len(set(keys)):
            yield jsonschema.ValidationError("数组存在重复项。")


FastValidator = jsonschema.validators.extend(jsonschema.Draft202012Validator, {"uniqueItems": unique_items})


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"重复 JSON 键：{key}")
        result[key] = value
    return result


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=unique_object,
                      parse_constant=lambda value: (_ for _ in ()).throw(ValueError(f"非有限数：{value}")))


def semantics(materials, world, scene):
    entries = materials["materials"]
    ids = [m["id"] for m in entries]
    names = [m["name"] for m in entries]
    assert len(ids) == len(set(ids)) and len(names) == len(set(names)), "材料 ID/name 重复"
    assert scene["materialSetId"] == materials["materialSetId"], "材料集合不一致"
    assert scene["materialsFile"] == "materials.json" and scene["worldConfig"] == "world_config.json"
    table = {m["id"]: m for m in entries}
    positions = [(c["y"], c["x"]) for c in scene["cells"]]
    assert positions == sorted(set(positions)), "坐标重复或未按 y/x 排序"
    assert len(positions) <= world["limits"]["maxMaterialCells"], "初态格数超限"
    cells = {(c["x"], c["y"]): c["materialId"] for c in scene["cells"]}
    for (x, y), material in cells.items():
        assert 0 <= x < world["width"] and 0 <= y < world["height"], "坐标越界"
        assert material in table, "无效材料引用"
    for name, tag in [("fixedCells", "structure"), ("initialBurning", "burnable")]:
        markers = [(p["y"], p["x"]) for p in scene[name]]
        assert markers == sorted(set(markers)), "标记重复或未排序"
        for y, x in markers:
            assert (x, y) in cells and tag in table[cells[x, y]]["tags"], "标记不适用"


def main():
    # 小样本与原库等价性核对；其余Schema关键字继续使用原2020-12实现。
    for sample in [[True, 1], [False, 0], [1, 1.0], [[1], [1.0]],
                   [{"x": 1}, {"x": 1.0}], [{"x": 1, "y": 2}, {"y": 2, "x": 1}], [None, None]]:
        schema = {"uniqueItems": True}
        assert FastValidator(schema).is_valid(sample) == jsonschema.Draft202012Validator(schema).is_valid(sample)
    filenames = ["materials", "world_config", "scene"]
    values, validators, hashes = {}, {}, {}
    for name in filenames:
        path = ROOT / "docs" / "examples" / f"{name}.json"
        schema = read_json(ROOT / "docs" / "schemas" / f"{name}.schema.json")
        jsonschema.Draft202012Validator.check_schema(schema)
        validators[name] = FastValidator(schema)
        values[name] = read_json(path)
        validators[name].validate(values[name])
        hashes[path.name] = hashlib.sha256(path.read_bytes()).hexdigest()
    semantics(values["materials"], values["world_config"], values["scene"])
    assert [m["id"] for m in values["materials"]["materials"]] == [101, 102, 103, 104]
    assert len(values["scene"]["cells"]) == 175
    assert len(values["scene"]["fixedCells"]) == 2 and len(values["scene"]["initialBurning"]) == 1

    cases = {
        "缺字段": lambda v: v["materials"]["materials"][0].pop("name"),
        "重复ID": lambda v: v["materials"]["materials"][1].update(id=101),
        "重复名称": lambda v: v["materials"]["materials"][1].update(name="水"),
        "未知标签": lambda v: v["materials"]["materials"][0]["tags"].append("unknown"),
        "类别错配": lambda v: v["materials"]["materials"][0].update(kind="solid"),
        "非法参数": lambda v: v["materials"]["materials"][3]["ruleParameters"]["burnable"].update(fuelTicks=0),
        "集合不匹配": lambda v: v["scene"].update(materialSetId="wrong"),
        "坐标越界": lambda v: v["scene"]["cells"][0].update(x=v["world_config"]["width"]),
        "未知字段": lambda v: v["world_config"].update(extra=True),
        "不支持版本": lambda v: v["world_config"].update(schemaVersion=2),
        "未知材料": lambda v: v["scene"]["cells"][0].update(materialId=65535),
    }
    rejected = []
    for case, change in cases.items():
        candidate = deepcopy(values)
        change(candidate)
        try:
            for name in filenames:
                validators[name].validate(candidate[name])
            semantics(candidate["materials"], candidate["world_config"], candidate["scene"])
        except (AssertionError, jsonschema.ValidationError, ValueError):
            rejected.append(case)
        else:
            raise AssertionError(f"反例未拒绝：{case}")
    try:
        json.loads('{"schemaVersion":1,"schemaVersion":1}', object_pairs_hook=unique_object)
    except ValueError:
        rejected.append("重复JSON键")
    else:
        raise AssertionError("重复JSON键未拒绝")
    report = {
        "验收ID": "M00-01", "结果": "通过（工具校验）", "jsonschema版本": importlib.metadata.version("jsonschema"),
        "材料ID": [101, 102, 103, 104], "格数": 175, "固定点数": 2, "初燃点数": 1,
        "逐材料格数": dict(Counter(c["materialId"] for c in values["scene"]["cells"])),
        "已拒绝反例": rejected, "配置SHA256": hashes, "限制": "未调用M01 Unity生产加载器；不代表PC包验证通过。"
    }
    fixture_path = ROOT / "docs" / "validation" / "m00-fixtures.json"
    if fixture_path.exists():
        fixtures = read_json(fixture_path)
        scenarios = fixtures["scenarios"]
        assert len(scenarios) == 31 and len({s["id"] for s in scenarios}) == 31
        for scenario in scenarios:
            validators["world_config"].validate(scenario["config"])
            validators["scene"].validate(scenario["scene"])
            semantics(values["materials"], scenario["config"], scenario["scene"])
        validators["materials"].validate(fixtures["T9材料副本"])
        restored = deepcopy(fixtures["T9材料副本"])
        restored["materials"][3]["ruleParameters"]["burnable"] = values["materials"]["materials"][3]["ruleParameters"]["burnable"]
        assert restored == values["materials"], "T9副本包含额外差异"
        report["导出夹具校验"] = {"场景数": 31, "结果": "世界/场景Schema与跨文件语义通过", "SHA256": hashlib.sha256(fixture_path.read_bytes()).hexdigest()}
    output = ROOT / "docs" / "validation" / "m00-baseline-result.json"
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
