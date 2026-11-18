using CesiumLoader.SDK.Manifests;

[assembly: ModManifest(
    "对队友进行大调查",
    "1.2.2",
    "TenkinMori",
    "别人隐藏的资料和战绩照样摊开看；开局静默比对当前房间的近期战绩，列出疑似组队的组合。",
    Permissions = ModPermission.ReadGameState | ModPermission.ModifyGameState,
    SdkVersion = "3.8.1")]
