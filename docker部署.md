# S4A21 + GM Tool Docker 完整部署教程

本项目基于 Docker 和 Docker Compose，提供 **S4A21 游戏服务端**（DNF 模拟器）及配套的 **GM 管理工具** 的一键部署方案。  
本教程适用于 Linux、macOS 以及 Windows（需安装 Docker Desktop）。

---

## 📋 前提条件

| 项目 | 要求 |
|------|------|
| 操作系统 | Linux（推荐 Ubuntu 20.04+）、macOS、Windows（WSL2 或 Docker Desktop） |
| Docker | 版本 20.10+，已安装 `docker compose`（V2） |
| 客户端文件 | 需要准备 S4A21 客户端的 `Script.pvf` 和 `ImagePacks2` 文件夹（用于 GM 工具显示图标） |
| 网络 | 服务器需开放相应端口（TCP/UDP），且能够拉取 `ghcr.io` 镜像（可配置镜像加速） |

---

## 🚀 快速开始

### 1. 安装 Docker（以 Ubuntu 为例）

```bash
# 更新软件包索引
sudo apt update

# 安装依赖
sudo apt install -y apt-transport-https ca-certificates curl software-properties-common

# 添加 Docker 官方 GPG 密钥
curl -fsSL https://download.docker.com/linux/ubuntu/gpg | sudo gpg --dearmor -o /usr/share/keyrings/docker-archive-keyring.gpg

# 添加 Docker 软件源
echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/docker-archive-keyring.gpg] https://download.docker.com/linux/ubuntu $(lsb_release -cs) stable" | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null

# 安装 Docker 及 Compose 插件
sudo apt update
sudo apt install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin

# 将当前用户加入 docker 组（避免每次使用 sudo）
sudo usermod -aG docker $USER
newgrp docker
其他系统请参考 Docker 官方安装指南。

2. 准备项目目录结构
创建一个项目目录，并按照以下结构存放文件：

text
~/s4a21/
├── docker-compose.yml          # 服务编排文件
├── config.ini                  # GM 工具配置文件
├── data/                       # 数据库目录（服务端自动生成）
├── pvf/                        # PVF 文件目录
│   └── Script.pvf              # 从客户端复制（必须）
├── faketime/                   # 时间模拟库（可选，无需手动操作）
└── ImagePacks2/                # 客户端资源（可选，用于 GM 工具图标显示）
创建目录：

bash
mkdir -p ~/s4a21/{data,pvf,faketime}
cd ~/s4a21
3. 准备配置文件
📄 docker-compose.yml
将以下内容保存为 docker-compose.yml。请务必修改其中的 IP 地址（所有出现 127.0.0.1 的地方替换为你的服务器实际 IP，如 192.168.1.100）。

yaml
services:
  s4a21server:
    image: ghcr.io/oyxp233/s4a21server-docker:latest
    container_name: s4a21server
    restart: unless-stopped
    ports:
      # ---------- TCP 端口 ----------
      - "7000:7000"                 # 频道服务
      - "7001:7001"                 # 频道服务
      - "10011:10011"               # 游戏服务
      - "10161:10161"               # 频道100
      - "10068:10068"               # 自由决斗
      - "10200:10200"               # TCP
      # ---------- UDP 端口 ----------
      - "7001:7001/udp"             # 频道 (UDP)
      - "11011:11011/udp"           # 游戏 (UDP)
      - "11161:11161/udp"           # 频道100 (UDP)
      - "11068:11068/udp"           # 自由决斗 (UDP)
      # ---------- UDP 中继端口范围 ----------
      - "2311-2313:2311-2313/udp"   # UDP 中继初始端口
      - "30000-30255:30000-30255/udp"  # 组队中继
      - "30256-30511:30256-30511/udp"  # PvP 中继
    volumes:
      - ./faketime:/usr/local/lib/faketime
      - ./data:/app/Data            # 数据库位置
      - ./pvf:/app/Data/Pvf         # PVF文件位置
    environment:
      - LD_PRELOAD=/usr/local/lib/faketime/libfaketime.so.1
      - FAKETIME=@2010-01-01 00:00:00
      - FAKETIME_NO_CACHE=1
      - DONT_FAKE_MONOTONIC=1
      - SERVER_IP=192.168.1.100               # ⚠️ 修改为你的实际IP
      - DFO_FREE_DUEL_CHANNEL_LISTENER=true
      - DFO_UDP_RELAY=true
      - DFO_PVP_UDP_RELAY=true
      - DFO_UDP_RELAY_PUBLIC_IP=192.168.1.100 # ⚠️ 修改为你的实际IP
    command: ["--server-ip", "192.168.1.100"] # ⚠️ 修改为你的实际IP
    healthcheck:
      test: ["CMD", "pgrep", "DfoServer"]
      interval: 30s
      timeout: 10s
      retries: 3

  s4a21gmtool:
    image: ghcr.io/oyxp233/s4a21gmtool-docker:latest
    container_name: s4a21gmtool
    restart: unless-stopped
    ports:
      - "5051:5051"     # GM工具端口
    volumes:
      - ./config.ini:/app/config.ini
      - ./data:/data/Data
      - ./pvf:/data/Data/Pvf
      - /path/to/your/ImagePacks2:/app/ImagePacks2:ro   # ⚠️ 修改为客户端 ImagePacks2 的实际路径
    environment:
      - DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
    command: ["--server-bin", "/data"]
    depends_on:
      - s4a21server
关键修改点：

将所有 127.0.0.1 替换为服务器的公网或局域网 IP。

将 ImagePacks2 的挂载源路径替换为你本地客户端的 ImagePacks2 文件夹路径（若不需要图标显示可删除此行）。

📄 config.ini
将以下内容保存为 config.ini（GM 工具配置）。注意密码请修改为强密码。

ini
# DFO GM 工具配置文件
allow_remote_access=true
listen_port=5051
remote_password=your_strong_password_here   # 至少8位
database_path=/data/Data/inventory.db
pvf_path=/data/Data/Pvf/Script.pvf
imagepacks_path=/app/ImagePacks2
说明：

allow_remote_access=true 允许局域网/外网访问（需配合密码）。

路径均为容器内路径，与 docker-compose.yml 中的挂载对应。

4. 复制客户端文件
将客户端中的 Script.pvf 复制到 ./pvf/ 目录：

bash
cp /path/to/client/Script.pvf ./pvf/
若需要 GM 工具显示道具图标，请将客户端 ImagePacks2 文件夹复制或挂载到指定路径（已在 docker-compose.yml 中配置）。

🔧 启动服务
拉取镜像并启动
bash
cd ~/s4a21
docker compose up -d
查看运行状态
bash
docker compose ps
两个容器 s4a21server 和 s4a21gmtool 应均为 Up 状态。

查看日志
bash
docker compose logs -f
✅ 验证部署
1. 检查服务端端口监听
服务端会监听以下端口（部分）：

TCP: 7000, 7001, 10011, 10161, 10068, 10200

UDP: 7001, 11011, 11161, 11068, 2311-2313, 30000-30511

可使用 netstat -tulpn | grep -E "7000|10011|11011" 验证。

2. 访问 GM 工具
浏览器打开 http://<你的服务器IP>:5051，输入 config.ini 中设置的密码即可登录。

🎮 客户端配置
将服务端的 Script.pvf 复制到游戏客户端根目录（覆盖原文件）。

修改客户端 Config.ini 文件，将服务器地址改为你的服务器 IP（例如 192.168.1.100）。

启动客户端，进入游戏测试。

🛠 常用命令
操作	命令
启动所有服务	docker compose up -d
停止所有服务	docker compose down
重启所有服务	docker compose restart
查看实时日志	docker compose logs -f
仅重启服务端	docker compose restart s4a21server
仅重启 GM 工具	docker compose restart s4a21gmtool
进入容器终端	docker exec -it s4a21server /bin/bash
⚠️ 注意事项
IP 地址：所有配置中的 SERVER_IP、DFO_UDP_RELAY_PUBLIC_IP 以及 command 中的 IP 必须填写服务器的真实 IP，不能是 127.0.0.1，否则外网客户端无法连接。

安全：GM 工具使用 HTTP 协议，请勿直接暴露到公网。建议使用 VPN、SSH 隧道或反向代理（如 Nginx + HTTPS）。

密码：config.ini 中的密码以明文存储，请限制该文件的读取权限（chmod 600 config.ini）。

数据库：首次启动时，数据库 data/inventory.db 会自动根据 Sqlite/item_schema.sql 创建。

镜像加速：如果拉取 ghcr.io 镜像缓慢，可配置 Docker 镜像加速器（如阿里云、中科大）。

项目状态：该项目仍处于研究完善阶段，可能存在 BUG，仅供学习交流使用。

🧪 故障排查
现象	可能原因	解决方式
容器启动失败	挂载路径不存在	检查 docker-compose.yml 中所有 ./ 开头的路径是否存在，创建缺失目录
GM 工具无法访问	allow_remote_access=false	改为 true 并设置密码
客户端连接超时	IP 配置错误或防火墙拦截	检查 SERVER_IP 是否正确；开放相关端口（TCP+UDP）
游戏提示“数据库错误”	inventory.db 损坏或权限不足	停止服务，删除 data/inventory.db，重启自动重建
GM 工具登录后无道具图标	ImagePacks2 挂载路径不正确	检查 docker-compose.yml 中 ImagePacks2 的挂载源路径是否存在且包含 .NPK 文件
端口冲突	主机端口已被占用	修改 docker-compose.yml 中左侧主机端口（如 "5052:5051"）
