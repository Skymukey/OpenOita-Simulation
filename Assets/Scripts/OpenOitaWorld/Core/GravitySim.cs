// 旧逐格速度 Job 已退役；重力由 M06 独立物理场景推进。
// 保留类型及资产 GUID，阻止调用者绕过统一事务写入。
[System.Obsolete("逐格重力已退役，请接入 M06 的 IPhysicsStepper。", true)]
public struct GravitySim
{
}
