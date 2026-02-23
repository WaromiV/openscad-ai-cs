cmake_minimum_required(VERSION 3.16)
project(cgal_worker LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

find_package(CGAL REQUIRED)

add_executable(cgal_worker
    src/main.cpp
)

target_link_libraries(cgal_worker PRIVATE CGAL::CGAL)

install(TARGETS cgal_worker DESTINATION bin)
