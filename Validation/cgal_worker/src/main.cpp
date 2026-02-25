#include <CGAL/Polygon_mesh_processing/connected_components.h>
#include <CGAL/Polygon_mesh_processing/polygon_soup_to_polygon_mesh.h>
#include <CGAL/Polygon_mesh_processing/repair_polygon_soup.h>
#include <CGAL/Polygon_mesh_processing/orient_polygon_soup.h>
#include <CGAL/Polygon_mesh_processing/self_intersections.h>
#include <CGAL/Simple_cartesian.h>
#include <CGAL/Surface_mesh.h>
#include <CGAL/IO/polygon_mesh_io.h>
#include <CGAL/IO/STL.h>

#include <iostream>
#include <sstream>
#include <string>
#include <vector>

namespace PMP = CGAL::Polygon_mesh_processing;

using Kernel = CGAL::Simple_cartesian<double>;
using Point = Kernel::Point_3;
using Mesh = CGAL::Surface_mesh<Point>;
using FaceDescriptor = boost::graph_traits<Mesh>::face_descriptor;

static std::string json_escape(const std::string& s) {
  std::ostringstream out;
  for (char c : s) {
    switch (c) {
      case '\\': out << "\\\\"; break;
      case '"': out << "\\\""; break;
      case '\n': out << "\\n"; break;
      case '\r': out << "\\r"; break;
      case '\t': out << "\\t"; break;
      default: out << c; break;
    }
  }
  return out.str();
}

int main(int argc, char** argv) {
  if (argc < 2) {
    std::cout << "{\"ok\":false,\"engine\":\"cgal\",\"warnings\":[{\"code\":\"BAD_ARGS\",\"severity\":\"high\",\"message\":\"Expected STL path argument\"}],\"metrics\":{}}\n";
    return 2;
  }

  const std::string stl_path = argv[1];
  Mesh mesh;
  std::vector<Point> points;
  std::vector<std::array<std::size_t, 3>> facets;

  auto read_soup = [&](bool binary_mode) {
    points.clear();
    facets.clear();
    return CGAL::IO::read_STL(
      stl_path,
      points,
      facets,
      CGAL::parameters::use_binary_mode(binary_mode)
    );
  };

  if (!read_soup(false) && !read_soup(true)) {
    std::cout << "{\"ok\":false,\"engine\":\"cgal\",\"warnings\":[{\"code\":\"MESH_READ_FAILED\",\"severity\":\"high\",\"message\":\"Failed to read mesh from "
              << json_escape(stl_path)
              << "\"}],\"metrics\":{}}\n";
    return 3;
  }

  if (points.empty() || facets.empty()) {
    std::cout << "{\"ok\":false,\"engine\":\"cgal\",\"warnings\":[{\"code\":\"MESH_READ_FAILED\",\"severity\":\"high\",\"message\":\"Mesh data empty for "
              << json_escape(stl_path)
              << "\"}],\"metrics\":{}}\n";
    return 3;
  }

  PMP::repair_polygon_soup(points, facets);

  PMP::orient_polygon_soup(points, facets);
  PMP::polygon_soup_to_polygon_mesh(points, facets, mesh);
  if (CGAL::is_empty(mesh)) {
    std::cout << "{\"ok\":false,\"engine\":\"cgal\",\"warnings\":[{\"code\":\"MESH_READ_FAILED\",\"severity\":\"high\",\"message\":\"Failed to build mesh from "
              << json_escape(stl_path)
              << "\"}],\"metrics\":{}}\n";
    return 3;
  }

  std::vector<std::size_t> fcc(num_faces(mesh));
  auto fcm = boost::make_iterator_property_map(fcc.begin(), get(boost::face_index, mesh));
  std::size_t components = PMP::connected_components(mesh, fcm);

  std::vector<std::pair<FaceDescriptor, FaceDescriptor>> intersections;
  PMP::self_intersections(mesh, std::back_inserter(intersections));

  bool ok = true;
  std::ostringstream warnings;
  bool first_warning = true;

  auto push_warning = [&](const std::string& code, const std::string& severity, const std::string& message, const std::string& suggested_fix) {
    if (!first_warning) {
      warnings << ",";
    }
    first_warning = false;
    warnings << "{\"code\":\"" << code
             << "\",\"severity\":\"" << severity
             << "\",\"message\":\"" << json_escape(message)
             << "\",\"suggested_fix\":\"" << json_escape(suggested_fix)
             << "\"}";
  };

  if (components > 1) {
    ok = false;
    push_warning(
      "DETACHED_PARTS",
      "high",
      "Mesh has disconnected components (possible detached chair legs).",
      "Ensure all load-bearing parts intersect and are united with boolean union."
    );
  }

  if (!intersections.empty()) {
    ok = false;
    push_warning(
      "SELF_INTERSECTIONS",
      "high",
      "Mesh has self-intersections.",
      "Adjust booleans and clearances; avoid coplanar overlaps."
    );
  }

  std::cout << "{"
            << "\"ok\":" << (ok ? "true" : "false")
            << ",\"engine\":\"cgal\""
            << ",\"warnings\":[" << warnings.str() << "]"
            << ",\"metrics\":{"
            << "\"connected_components\":" << components << ","
            << "\"self_intersections\":" << intersections.size()
            << "}"
            << "}\n";

  return 0;
}
