# Chemistry Experiment - MySQL 数据库集成部署指南

## 📂 文件说明

| 文件 | 说明 |
|------|------|
| `Database/init_database.sql` | 数据库初始化 SQL 脚本 |
| `Assets/Scripts/Database/DatabaseConfig.cs` | 数据库连接配置类 |
| `Assets/Scripts/Database/ReagentData.cs` | 试剂/反应/实验数据模型 |
| `Assets/Scripts/Database/DatabaseManager.cs` | 数据库管理器（单例，CRUD 操作） |
| `Assets/Scripts/Database/ReagentUIManager.cs` | 试剂 UI 交互脚本 |

---

## 第一步：安装 **MySql.Data.dll** 到 Unity

Unity 需要 `MySql.Data.dll` 才能连接 MySQL。

### 方法 A：用 NuGet（推荐）

1. 访问 https://www.nuget.org/packages/MySql.Data/
2. 下载最新版 `.nupkg` 文件
3. 用解压软件（7-Zip / WinRAR）打开 `.nupkg`，提取 `lib/netstandard2.0/MySql.Data.dll`
4. 将 `MySql.Data.dll` 放到：`Assets/Plugins/` 目录下

> 如果 Unity 报错 "API not supported"，改用 `lib/net452/MySql.Data.dll`

### 方法 B：用 MySQL Installer

1. 下载 MySQL Installer：https://dev.mysql.com/downloads/installer/
2. 安装时勾选 **Connector/NET**
3. 安装完成后，在 `C:\Program Files (x86)\MySQL\MySQL Connector NET 8.x\Assemblies\` 找到 `MySql.Data.dll`
4. 复制到 `Assets/Plugins/`

### 方法 C：用 mysql-connector-net 直接下载

1. 下载 mysql-connector-net-x.x.x.msi
2. 安装后从安装目录获取 `MySql.Data.dll`，放到 `Assets/Plugins/`

---

## 第二步：初始化 MySQL 数据库

### 前提：确保 MySQL 服务已启动

```bash
# 检查 MySQL 是否运行
mysql --version

# 如未启动（Windows）
net start mysql
# 或
net start mysql80
```

### 运行初始化脚本

```bash
# 登录 MySQL
mysql -u root -p

# 在 MySQL 命令行中执行
source "D:/Unity/Projects/Chemistry Experiment/Database/init_database.sql"

# 或者直接在命令行执行
mysql -u root -p < "D:/Unity/Projects/Chemistry Experiment/Database/init_database.sql"
```

> **注意**：脚本会创建 `chemistry_db` 数据库，并插入 25 种常见化学试剂的示例数据。

### 验证数据是否导入成功

```sql
USE chemistry_db;
SELECT COUNT(*) FROM reagents;
SELECT name, formula, category FROM reagents LIMIT 5;
```

---

## 第三步：Unity 场景配置

### 3.1 添加 DatabaseManager

1. 在 Hierarchy 中创建空 GameObject，命名为 `DatabaseManager`
2. 将 `DatabaseManager.cs` 脚本拖到该 GameObject 上
3. 在 Inspector 中配置连接参数：

| 参数 | 默认值 | 说明 |
|------|--------|------|
| Host | `localhost` | MySQL 服务器地址 |
| Port | `3306` | MySQL 端口 |
| Database | `chemistry_db` | 数据库名 |
| Username | `root` | 用户名 |
| Password | (空) | 密码（根据你的 MySQL 设置填写）|
| Connection Timeout | `10` | 连接超时（秒）|

### 3.2 添加 ReagentUIManager（可选，用于 UI 展示）

1. 在 Canvas 下创建空 GameObject，命名为 `ReagentUIManager`
2. 将 `ReagentUIManager.cs` 脚本拖到该 GameObject 上
3. 在 Inspector 中分配 UI 元素：
   - `Reagent List Content` → 列表容器（Scroll View 的 Content）
   - `Reagent Item Prefab` → 每个试剂的 UI 预制体（可选）
   - `Detail Text` → 显示试剂详情的 TextMeshPro 组件
   - `Status Text` → 显示连接状态的 TextMeshPro 组件
   - `Search Input Field` → 搜索输入框
   - `Category Dropdown` → 类别筛选下拉框
   - `Connect Button` → 连接按钮
   - `Refresh Button` → 刷新按钮

### 3.3 自动连接

勾选 `ReagentUIManager` 中的 `Auto Connect On Start`，即可在游戏启动时自动连接数据库。

---

## 第四步：代码中使用

### 查询所有试剂

```csharp
var reagents = DatabaseManager.Instance.GetAllReagents();
foreach (var r in reagents)
{
    Debug.Log(r.name + " " + r.formula);
}
```

### 按类别查询

```csharp
var acids = DatabaseManager.Instance.GetReagentsByCategory("酸");
```

### 搜索试剂

```csharp
var results = DatabaseManager.Instance.SearchReagents("盐酸");
```

### 添加新试剂

```csharp
var newReagent = new ReagentData
{
    name = "新试剂",
    nameEn = "New Reagent",
    formula = "XyZ",
    category = "其他",
    state = "固体",
    color = "白色",
    hazardLevel = 0,
    description = "这是一个新试剂"
};
int newId = DatabaseManager.Instance.AddReagent(newReagent);
```

### 查询两种试剂之间的反应

```csharp
// 查看盐酸和锌之间是否可以发生反应
var reactions = DatabaseManager.Instance.GetReactionsBetweenReagents(1, 24);
```

---

## 数据库表结构说明

### `reagents` 表 - 化学试剂

| 字段 | 类型 | 说明 |
|------|------|------|
| id | INT (PK) | 试剂 ID |
| name | VARCHAR(100) | 试剂名称（中文）|
| name_en | VARCHAR(100) | 英文名称 |
| formula | VARCHAR(50) | 化学式 |
| molecular_weight | DECIMAL(10,4) | 分子量 |
| category | VARCHAR(50) | 类别 |
| state | VARCHAR(20) | 常温状态 |
| color | VARCHAR(50) | 颜色 |
| density | DECIMAL(10,4) | 密度 |
| hazard_level | TINYINT | 危险等级 0-3 |
| description | TEXT | 详细描述 |

### `reactions` 表 - 化学反应

存储试剂之间的化学反应，包括方程式、反应类型、现象等。

### `experiments` 表 - 化学实验

存储实验信息，包括名称、步骤（JSON）、难度等。

---

## 常见问题

### Q：Unity 报错 `MySql.Data.MySqlClient.MySqlException`

→ 检查 MySQL 服务是否启动，用户名密码是否正确。

### Q：中文乱码

→ 确认数据库使用 `utf8mb4` 字符集，连接字符串中已包含 `CharacterSet=utf8mb4`。

### Q：Unity 构建后无法连接数据库

→ Unity 直连方式在构建后会将数据库凭证打包在客户端，**不安全**。建议改用 Web API 中间层（Node.js / Python Flask）代理数据库请求。

### Q：找不到 `MySql.Data.dll`

→ 确认文件已放在 `Assets/Plugins/` 目录下，且 Unity 已完成编译（等待底部进度条消失）。

---

## 数据库中的示例数据

已预置以下试剂（共 25 种）：
- **酸**：盐酸、硫酸、硝酸
- **碱**：氢氧化钠、氢氧化钙、氨水
- **盐**：氯化钠、碳酸钠、碳酸钙、硫酸铜、氯化钡
- **氧化物**：氧化钙
- **指示剂**：石蕊、酚酞
- **有机物**：乙醇、乙酸
- **其他**：过氧化氢、高锰酸钾、二氧化锰、锌、铁

已预置 8 个化学反应方程式和 3 个示例实验。

---

## 安全提示 ⚠

**Unity 直连 MySQL 仅适用于开发/学习环境！**

如果项目需要发布，请务必：
1. 改用 Web API 中间层（Flask / Express / ASP.NET Core）
2. 不要将数据库凭证存储在客户端代码中
3. 对用户输入做 SQL 参数化（本项目中已使用参数化查询）

---
