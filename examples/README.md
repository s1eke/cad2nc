# 示例与复现

## 当前 2.6.4 示例

输入：[lnc_sheet_mm.dxf](../tests/fixtures/lnc_sheet_mm.dxf)。这是仓库内的合成测试图纸，母板 1220 × 2440 mm，原点 `(1000, 2000)`，包含两个加工轮廓。

输出：[NC](lnc_sheet_mm-2.6.4.nc) · [摘要](lnc_sheet_mm-2.6.4.json)。默认留皮为 0，关闭分层，只有最终 `Z-0.10` 一个深度层；稳定优先不自动增加深度层。该最小 DXF 的兼容提示已在测试中核对，生成命令显式接受这些提示。

在仓库根目录完成 `./build.ps1` 后运行：

```powershell
# 重新生成并对公开摘要中的路径脱敏
./scripts/generate_example.ps1
# 只验证，不改写已提交示例
./scripts/generate_example.ps1 -VerifyOnly
```

开发时也可直接使用 CLI（此命令保留实际绝对路径，输出留在被忽略的 build 目录）：

```powershell
dotnet run --project src/Cad2Nc.Cli -c Release -- --convert tests/fixtures/lnc_sheet_mm.dxf --output build/example.nc --summary build/example.json --accept-import-diagnostics
```

## 2.6.1 历史真实图纸示例

[历史 NC](异型板图_v2默认参数.nc) · [历史摘要](异型板图_v2验证摘要.json)。摘要实际记录版本为 **2.6.1**；本次仅脱敏 `source` 和 `output` 路径，保留其余历史数据及 NC 原文。这组文件不是 2.6.4 重新生成的验收结果。原始私人 DWG 未公开，因此不能仅凭本仓库复现这组历史输出。

示例用于检查软件输出；现场加工须按使用说明核对图纸、工艺和机床设置。
