-- ============================================================
-- 化学实验模拟系统 - MySQL 数据库初始化脚本
-- ============================================================

-- 创建数据库
CREATE DATABASE IF NOT EXISTS chemistry_db
    DEFAULT CHARACTER SET utf8mb4
    DEFAULT COLLATE utf8mb4_unicode_ci;

USE chemistry_db;

-- ============================================================
-- 1. 试剂表 (reagents) - 存储所有化学实验试剂信息
-- ============================================================
CREATE TABLE IF NOT EXISTS reagents (
    id              INT AUTO_INCREMENT PRIMARY KEY COMMENT '试剂ID',
    name            VARCHAR(100)    NOT NULL       COMMENT '试剂名称',
    name_en         VARCHAR(100)                   COMMENT '英文名称',
    formula         VARCHAR(50)     NOT NULL       COMMENT '化学式',
    molecular_weight DECIMAL(10,4)                 COMMENT '分子量 (g/mol)',
    category        VARCHAR(50)     NOT NULL       COMMENT '类别: 酸/碱/盐/氧化物/有机物/指示剂/其他',
    state           VARCHAR(20)     NOT NULL       COMMENT '常温状态: 固体/液体/气体',
    color           VARCHAR(50)                    COMMENT '颜色描述',
    density         DECIMAL(10,4)                 COMMENT '密度 (g/cm³ 或 g/L)',
    melting_point   DECIMAL(10,2)                 COMMENT '熔点 (°C)',
    boiling_point   DECIMAL(10,2)                 COMMENT '沸点 (°C)',
    solubility      VARCHAR(200)                  COMMENT '溶解性描述',
    hazard_level    TINYINT        DEFAULT 0      COMMENT '危险等级: 0=安全 1=低危 2=中危 3=高危',
    hazard_info     VARCHAR(500)                  COMMENT '危险信息/GHS警示',
    storage_condition VARCHAR(200)                 COMMENT '存储条件',
    description     TEXT                           COMMENT '详细描述',
    icon_path       VARCHAR(255)                   COMMENT 'Unity中图标资源路径',
    created_at      TIMESTAMP      DEFAULT CURRENT_TIMESTAMP,
    updated_at      TIMESTAMP      DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    INDEX idx_category (category),
    INDEX idx_name (name),
    INDEX idx_formula (formula)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='化学试剂数据表';

-- ============================================================
-- 2. 实验表 (experiments) - 存储化学实验信息
-- ============================================================
CREATE TABLE IF NOT EXISTS experiments (
    id              INT AUTO_INCREMENT PRIMARY KEY COMMENT '实验ID',
    name            VARCHAR(200)   NOT NULL       COMMENT '实验名称',
    description     TEXT                           COMMENT '实验描述',
    type            VARCHAR(50)                   COMMENT '实验类型: 验证性/探究性/制备',
    difficulty      TINYINT        DEFAULT 1       COMMENT '难度: 1-5',
    duration        INT                            COMMENT '预计时长(分钟)',
    safety_notes    TEXT                           COMMENT '安全注意事项',
    steps_json      JSON                           COMMENT '实验步骤(JSON格式)',
    created_at      TIMESTAMP      DEFAULT CURRENT_TIMESTAMP,
    updated_at      TIMESTAMP      DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    INDEX idx_type (type),
    INDEX idx_difficulty (difficulty)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='化学实验表';

-- ============================================================
-- 3. 实验所需试剂关联表 (experiment_reagents)
-- ============================================================
CREATE TABLE IF NOT EXISTS experiment_reagents (
    id              INT AUTO_INCREMENT PRIMARY KEY,
    experiment_id   INT NOT NULL COMMENT '实验ID',
    reagent_id      INT NOT NULL COMMENT '试剂ID',
    amount          VARCHAR(100) COMMENT '用量描述',
    role            VARCHAR(50)  COMMENT '角色: 反应物/催化剂/指示剂/溶剂',
    FOREIGN KEY (experiment_id) REFERENCES experiments(id) ON DELETE CASCADE,
    FOREIGN KEY (reagent_id) REFERENCES reagents(id) ON DELETE CASCADE,
    UNIQUE KEY uk_exp_reagent (experiment_id, reagent_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='实验-试剂关联表';

-- ============================================================
-- 4. 化学反应表 (reactions) - 存储试剂间的化学反应
-- ============================================================
CREATE TABLE IF NOT EXISTS reactions (
    id              INT AUTO_INCREMENT PRIMARY KEY COMMENT '反应ID',
    name            VARCHAR(200)   NOT NULL       COMMENT '反应名称',
    equation        VARCHAR(500)   NOT NULL       COMMENT '化学方程式',
    reaction_type   VARCHAR(50)                   COMMENT '反应类型: 化合/分解/置换/复分解/氧化还原/中和',
    phenomenon      TEXT                           COMMENT '实验现象描述',
    conditions      VARCHAR(200)                  COMMENT '反应条件(加热/催化/光照等)',
    is_exothermic   BOOLEAN        DEFAULT FALSE  COMMENT '是否放热',
    is_dangerous    BOOLEAN        DEFAULT FALSE  COMMENT '是否危险',
    notes           TEXT                           COMMENT '注意事项',
    created_at      TIMESTAMP      DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_type (reaction_type)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='化学反应表';

-- ============================================================
-- 5. 反应物关联表 (reaction_reagents) - 反应涉及的试剂
-- ============================================================
CREATE TABLE IF NOT EXISTS reaction_reagents (
    id              INT AUTO_INCREMENT PRIMARY KEY,
    reaction_id     INT NOT NULL COMMENT '反应ID',
    reagent_id      INT NOT NULL COMMENT '试剂ID',
    side            ENUM('reactant', 'product') NOT NULL COMMENT '反应物/生成物',
    coefficient     INT DEFAULT 1 COMMENT '化学计量数',
    FOREIGN KEY (reaction_id) REFERENCES reactions(id) ON DELETE CASCADE,
    FOREIGN KEY (reagent_id) REFERENCES reagents(id) ON DELETE CASCADE,
    INDEX idx_reaction (reaction_id),
    INDEX idx_reagent (reagent_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='反应-试剂关联表';

-- ============================================================
-- 插入示例试剂数据
-- ============================================================
INSERT INTO reagents (name, name_en, formula, molecular_weight, category, state, color, density, melting_point, boiling_point, solubility, hazard_level, hazard_info, storage_condition, description) VALUES
-- 常见酸
('盐酸', 'Hydrochloric Acid', 'HCl', 36.46, '酸', '液体', '无色', 1.19, -114.2, -85.0, '与水任意比互溶', 2, '腐蚀性，刺激呼吸道', '密封存于阴凉通风处', '常用强酸，实验室广泛使用'),
('硫酸', 'Sulfuric Acid', 'H₂SO₄', 98.08, '酸', '液体', '无色', 1.84, 10.0, 337.0, '与水任意比互溶（放热）', 3, '强腐蚀性，遇水剧烈放热', '密封存于干燥处，严禁加水入酸', '最重要化工原料之一，强氧化性和脱水性'),
('硝酸', 'Nitric Acid', 'HNO₃', 63.01, '酸', '液体', '无色/发黄', 1.51, -42.0, 83.0, '与水任意比互溶', 3, '强腐蚀性和氧化性', '棕色瓶密封，避光阴凉处', '强氧化性酸，见光易分解'),

-- 常见碱
('氢氧化钠', 'Sodium Hydroxide', 'NaOH', 40.00, '碱', '固体', '白色', 2.13, 318.4, 1388.0, '极易溶于水（放热）', 2, '强腐蚀性，遇水放热', '密封防潮，塑料瓶储存', '俗称烧碱/火碱，实验室常用强碱'),
('氢氧化钙', 'Calcium Hydroxide', 'Ca(OH)₂', 74.09, '碱', '固体', '白色', 2.24, 580.0, NULL, '微溶于水(0.16g/100mL)', 1, '有腐蚀性', '密封干燥处', '俗称熟石灰/消石灰，用于检验CO₂'),
('氨水', 'Ammonia Solution', 'NH₃·H₂O', 35.05, '碱', '液体', '无色', 0.90, -77.7, -33.3, '与水任意比互溶', 2, '刺激性气味，腐蚀性', '密封阴凉处', '弱碱，用于检验离子和喷泉实验'),

-- 常见盐
('氯化钠', 'Sodium Chloride', 'NaCl', 58.44, '盐', '固体', '白色', 2.17, 801.0, 1413.0, '36g/100mL(20°C)', 0, '基本安全', '干燥处', '食盐的主要成分，可制备氯气和氢氧化钠'),
('碳酸钠', 'Sodium Carbonate', 'Na₂CO₃', 105.99, '盐', '固体', '白色', 2.54, 851.0, NULL, '21.5g/100mL(20°C)', 1, '碱性溶液有腐蚀性', '干燥密封处', '俗称纯碱/苏打，重要的化工原料'),
('碳酸钙', 'Calcium Carbonate', 'CaCO₃', 100.09, '盐', '固体', '白色', 2.71, 825.0, NULL, '不溶于水', 0, '基本安全', '干燥处', '石灰石/大理石主要成分，遇酸产生CO₂'),
('硫酸铜', 'Copper Sulfate', 'CuSO₄', 159.61, '盐', '固体', '白色/蓝色', 3.60, 200.0, NULL, '20.7g/100mL(20°C)五水合物', 2, '有毒，重金属盐', '密封干燥处', '无水物白色，五水合物蓝色，用于检验水'),
('氯化钡', 'Barium Chloride', 'BaCl₂', 208.23, '盐', '固体', '白色', 3.86, 962.0, 1560.0, '37.5g/100mL(20°C)', 3, '剧毒！钡盐中毒', '专人专柜管理', '用于检验SO₄²⁻离子，剧毒需严管'),

-- 氧化物
('氧化钙', 'Calcium Oxide', 'CaO', 56.08, '氧化物', '固体', '白色', 3.34, 2572.0, 2850.0, '与水反应生成Ca(OH)₂', 2, '遇水剧烈放热，腐蚀性', '密封防潮', '俗称生石灰，遇水变成熟石灰'),

-- 指示剂
('石蕊', 'Litmus', 'C₇H₇NO₄', 1550.0, '指示剂', '液体', '紫色', NULL, NULL, NULL, '溶于水/乙醇', 1, '基本安全', '避光阴凉处', '遇酸变红，遇碱变蓝，常用酸碱指示剂'),
('酚酞', 'Phenolphthalein', 'C₂₀H₁₄O₄', 318.32, '指示剂', '固体', '白色', 1.30, 261.0, NULL, '难溶于水，溶于乙醇', 1, '基本安全', '避光保存', '碱性溶液变红，酸性和中性无色'),

-- 有机物
('乙醇', 'Ethanol', 'C₂H₅OH', 46.07, '有机物', '液体', '无色', 0.79, -114.1, 78.4, '与水任意比互溶', 1, '易燃', '远离火源密封保存', '俗称酒精，常用溶剂和燃料'),
('乙酸', 'Acetic Acid', 'CH₃COOH', 60.05, '有机物', '液体', '无色', 1.05, 16.6, 117.9, '与水任意比互溶', 2, '腐蚀性，刺激性气味', '密封阴凉处', '俗称醋酸，食醋含3-5%乙酸'),

-- 其他
('过氧化氢', 'Hydrogen Peroxide', 'H₂O₂', 34.01, '其他', '液体', '无色', 1.45, -0.4, 150.2, '与水任意比互溶', 2, '强氧化性，腐蚀性', '棕色瓶避光冷藏', '俗称双氧水，常用于制取O₂'),
('高锰酸钾', 'Potassium Permanganate', 'KMnO₄', 158.03, '盐', '固体', '紫黑色', 2.70, 240.0, NULL, '6.4g/100mL(20°C)', 2, '强氧化性，腐蚀性', '避光密封', '强氧化剂，常用于制取O₂和消毒'),
('二氧化锰', 'Manganese Dioxide', 'MnO₂', 86.94, '氧化物', '固体', '黑色', 5.03, NULL, NULL, '不溶于水', 1, '粉尘有害', '干燥处', '常用作H₂O₂分解和KClO₃分解的催化剂'),
('锌', 'Zinc', 'Zn', 65.38, '其他', '固体', '银白色', 7.14, 419.5, 907.0, '不溶于水，溶于酸', 1, '粉尘有害', '干燥处', '活泼金属，常用于制取H₂'),
('铁', 'Iron', 'Fe', 55.85, '其他', '固体', '银白色', 7.87, 1538.0, 2862.0, '不溶于水，溶于酸', 1, '粉尘有害', '干燥处', '常见金属，与酸反应产生H₂');

-- ============================================================
-- 插入示例化学反应数据
-- ============================================================
INSERT INTO reactions (name, equation, reaction_type, phenomenon, conditions, is_exothermic, is_dangerous, notes) VALUES
('盐酸与氢氧化钠中和', 'HCl + NaOH → NaCl + H₂O', '中和', '溶液温度升高，无明显现象(加指示剂可见变色)', '常温', TRUE, FALSE, '经典中和反应，可用酚酞指示终点'),
('盐酸与碳酸钙反应', 'CaCO₃ + 2HCl → CaCl₂ + H₂O + CO₂↑', '复分解', '固体溶解，产生大量气泡', '常温', FALSE, FALSE, '实验室制取CO₂的常用方法'),
('锌与盐酸反应', 'Zn + 2HCl → ZnCl₂ + H₂↑', '置换', '锌粒溶解，产生气泡', '常温', TRUE, FALSE, '实验室制取H₂的常用方法，注意验纯'),
('过氧化氢分解', '2H₂O₂ →(MnO₂) 2H₂O + O₂↑', '分解', '产生大量气泡', 'MnO₂催化', TRUE, FALSE, '实验室制取O₂的常用方法'),
('铁与硫酸铜反应', 'Fe + CuSO₄ → FeSO₄ + Cu', '置换', '铁表面覆盖红色固体，溶液由蓝变浅绿', '常温', FALSE, FALSE, '典型的金属活动性顺序验证实验'),
('硫酸与氢氧化钠中和', 'H₂SO₄ + 2NaOH → Na₂SO₄ + 2H₂O', '中和', '溶液温度升高', '常温', TRUE, FALSE, '注意酸碱用量比例'),
('碳酸钠与盐酸反应', 'Na₂CO₃ + 2HCl → 2NaCl + H₂O + CO₂↑', '复分解', '产生大量气泡', '常温', FALSE, FALSE, '可用作灭火器原理演示'),
('铜与硝酸反应', '3Cu + 8HNO₃(稀) → 3Cu(NO₃)₂ + 2NO↑ + 4H₂O', '氧化还原', '铜溶解，溶液变蓝，产生无色气体(遇空气变红棕)', '常温', TRUE, TRUE, 'NO有毒！需在通风橱中进行');

-- ============================================================
-- 插入示例实验数据
-- ============================================================
INSERT INTO experiments (name, description, type, difficulty, duration, safety_notes, steps_json) VALUES
('酸碱中和反应', '利用盐酸和氢氧化钠进行中和反应，用酚酞指示剂判断反应终点', '验证性', 1, 15, '佩戴护目镜和手套，避免酸碱溅入眼睛', '[{"step":1,"desc":"取一支试管，加入2mL稀盐酸","equipment":"试管"},{"step":2,"desc":"滴加2滴酚酞指示剂","equipment":"滴管"},{"step":3,"desc":"用滴管逐滴加入NaOH溶液","equipment":"滴管"},{"step":4,"desc":"观察溶液颜色变化，当溶液变粉红色且半分钟内不褪色即为终点","equipment":"无"},{"step":5,"desc":"用手触摸试管外壁，感受温度变化","equipment":"无"}]'),
('实验室制取氧气', '利用过氧化氢在二氧化锰催化下分解制取氧气，并用排水法收集', '制备', 2, 25, 'H₂O₂有腐蚀性，避免接触皮肤', '[{"step":1,"desc":"检查装置气密性","equipment":"锥形瓶+导管"},{"step":2,"desc":"在锥形瓶中加入少量MnO₂","equipment":"药匙"},{"step":3,"desc":"通过分液漏斗加入H₂O₂溶液","equipment":"分液漏斗"},{"step":4,"desc":"待气泡均匀后用排水法收集气体","equipment":"集气瓶+水槽"},{"step":5,"desc":"用带火星木条验满","equipment":"木条"}]'),
('金属活动性验证', '通过铁与硫酸铜的置换反应验证金属活动性顺序', '验证性', 1, 10, '硫酸铜有毒，避免误食', '[{"step":1,"desc":"取一支试管，加入少量CuSO₄溶液","equipment":"试管"},{"step":2,"desc":"放入一枚铁钉","equipment":"镊子"},{"step":3,"desc":"观察铁钉表面变化和溶液颜色变化","equipment":"无"},{"step":4,"desc":"取出铁钉，观察表面覆盖的红色物质","equipment":"镊子"}]');

-- ============================================================
-- 关联实验与试剂
-- ============================================================
INSERT INTO experiment_reagents (experiment_id, reagent_id, amount, role) VALUES
-- 酸碱中和实验
(1, 1, '2mL 稀盐酸(1mol/L)', '反应物'),
(1, 4, 'NaOH溶液适量', '反应物'),
(1, 16, '2-3滴', '指示剂'),
-- 制取氧气实验
(2, 21, '30mL 3%双氧水', '反应物'),
(2, 23, '少量(约0.5g)', '催化剂'),
-- 金属活动性实验
(3, 25, '一枚铁钉', '反应物'),
(3, 10, '5mL CuSO₄溶液', '反应物');

-- ============================================================
-- 关联反应与试剂
-- ============================================================
INSERT INTO reaction_reagents (reaction_id, reagent_id, side, coefficient) VALUES
-- 盐酸+NaOH
(1, 1, 'reactant', 1),
(1, 4, 'reactant', 1),
(1, 7, 'product', 1),
-- CaCO₃+HCl
(2, 1, 'reactant', 2),
(2, 9, 'reactant', 1),
-- Zn+HCl
(3, 1, 'reactant', 2),
(3, 24, 'reactant', 1),
-- H₂O₂分解
(4, 21, 'reactant', 2),
(4, 23, 'reactant', 0),
-- Fe+CuSO₄
(5, 25, 'reactant', 1),
(5, 10, 'reactant', 1),
-- Na₂CO₃+HCl
(7, 1, 'reactant', 2),
(7, 8, 'reactant', 1);
