cmake_minimum_required(VERSION 3.20)
project(DeviantExportModelsNative LANGUAGES CXX)
find_package(Threads REQUIRED)

add_library(Deviant.Export.Models.Native SHARED
	xatlas_bridge.cpp
	vendor/xatlas/xatlas.cpp)

target_compile_features(Deviant.Export.Models.Native PRIVATE cxx_std_17)
target_include_directories(Deviant.Export.Models.Native
	PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}
	PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/vendor/xatlas)
target_compile_definitions(Deviant.Export.Models.Native PRIVATE
	DEVIANT_XATLAS_BRIDGE_BUILD=1
	NDEBUG
	XA_DEBUG=0
	XA_MULTITHREADED=0)
target_link_libraries(Deviant.Export.Models.Native PRIVATE Threads::Threads)
set_target_properties(Deviant.Export.Models.Native PROPERTIES
	CXX_VISIBILITY_PRESET hidden
	VISIBILITY_INLINES_HIDDEN YES)
