BUILD_DIR ?= build
PREFIX    ?= ../..
BIN_DIR   ?= $(PREFIX)/bins
STATIC    ?= ON

.PHONY: all clean install

all: $(BUILD_DIR)/fqctl

$(BUILD_DIR)/fqctl: src/main.cpp CMakeLists.txt
	cmake -S . -B $(BUILD_DIR) -DCMAKE_BUILD_TYPE=Release -DFQCTL_STATIC=$(STATIC)
	cmake --build $(BUILD_DIR) --config Release

install: $(BUILD_DIR)/fqctl
	mkdir -p $(BIN_DIR)
	cp -f $(BUILD_DIR)/fqctl $(BIN_DIR)/fqctl
	chmod 755 $(BIN_DIR)/fqctl

clean:
	rm -rf $(BUILD_DIR)
