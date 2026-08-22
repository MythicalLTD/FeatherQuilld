PROJECT      := FeatherQuilld
CONFIG       ?= Debug
CONFIG_FILE  ?= config.yml
PORT         ?= 8989
DOCKER_IMAGE ?= featherquilld
DOCKER_TAG   ?= latest

.PHONY: help restore build build-plugins run watch stop publish clean docker docker-run test fmt

help: ## Show this help
	@awk 'BEGIN {FS = ":.*##"; printf "Usage: make <target>\n\n"} /^[a-zA-Z_-]+:.*?##/ { printf "  %-14s %s\n", $$1, $$2 }' $(MAKEFILE_LIST)

restore: ## Restore NuGet packages
	dotnet restore FeatherQuilld.slnx

build: build-plugins ## Build host + plugins (CONFIG=Debug|Release)
	dotnet build FeatherQuilld.csproj -c $(CONFIG) --nologo

build-plugins: ## Build and deploy sample plugins
	dotnet build plugins/Hello/Hello.csproj -c $(CONFIG) --nologo

run: build ## Run the daemon (CONFIG_FILE=config.yml)
	FEATHERQUILLD_CONFIG=$(CONFIG_FILE) ASPNETCORE_ENVIRONMENT=Development \
		dotnet run --project FeatherQuilld.csproj -c $(CONFIG) --no-launch-profile -- --config $(CONFIG_FILE)

watch: ## Run with hot reload
	FEATHERQUILLD_CONFIG=$(CONFIG_FILE) ASPNETCORE_ENVIRONMENT=Development \
		dotnet watch --project FeatherQuilld.csproj run --no-launch-profile -- --config $(CONFIG_FILE)

stop: ## Kill whatever is listening on PORT (default 8989)
	@pids=$$(ss -ltnp 2>/dev/null | sed -n 's/.*:'$(PORT)' .*pid=\([0-9]*\).*/\1/p' | sort -u); \
	if [ -z "$$pids" ]; then echo "Nothing listening on $(PORT)"; exit 0; fi; \
	echo "Stopping PID(s) on $(PORT): $$pids"; \
	kill $$pids 2>/dev/null || true; \
	sleep 0.3; \
	kill -9 $$pids 2>/dev/null || true

publish: build-plugins ## Publish Release build to ./publish
	dotnet publish FeatherQuilld.csproj -c Release -o ./publish --nologo

clean: ## Remove build artifacts
	dotnet clean FeatherQuilld.slnx --nologo
	rm -rf ./bin ./obj ./publish

docker: ## Build Docker image
	docker build -t $(DOCKER_IMAGE):$(DOCKER_TAG) .

docker-run: ## Run Docker image on PORT (default 8989)
	docker run --rm -it \
		-p $(PORT):8989 \
		-v "$(CURDIR)/$(CONFIG_FILE):/etc/featherquilld/config.yml:ro" \
		-e FEATHERQUILLD_CONFIG=/etc/featherquilld/config.yml \
		$(DOCKER_IMAGE):$(DOCKER_TAG)

test: ## Run tests (when present)
	dotnet test -c $(CONFIG) --nologo

fmt: ## Format C# sources
	dotnet format FeatherQuilld.slnx
