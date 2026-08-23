# 阶段1：构建
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# 复制项目文件并还原依赖
COPY . .
RUN dotnet restore "Server/DfoServer.sln"

# 发布项目（生成自包含的可执行文件）
RUN dotnet publish "Server/DfoServer.sln" -c Release -r linux-x64 --self-contained true -o /app/publish

# 阶段2：运行时
FROM debian:bookworm-slim AS final
WORKDIR /app

# 安装运行时依赖
RUN apt-get update && apt-get install -y libicu72 libssl3 && rm -rf /var/lib/apt/lists/*

# 从构建阶段复制发布结果
COPY --from=build /app/publish .

# 暴露服务端口
EXPOSE 7001 10011

# 设置容器启动命令
ENTRYPOINT ["./DfoServer"]
CMD ["--server-ip", "auto"]
