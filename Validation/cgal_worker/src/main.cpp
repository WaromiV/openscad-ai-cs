#include <CGAL/Polygon_mesh_processing/bbox.h>
#include <CGAL/Polygon_mesh_processing/connected_components.h>
#include <CGAL/Polygon_mesh_processing/orient_polygon_soup.h>
#include <CGAL/Polygon_mesh_processing/polygon_soup_to_polygon_mesh.h>
#include <CGAL/Polygon_mesh_processing/repair_polygon_soup.h>
#include <CGAL/Polygon_mesh_processing/self_intersections.h>
#include <CGAL/Side_of_triangle_mesh.h>
#include <CGAL/Simple_cartesian.h>
#include <CGAL/Surface_mesh.h>
#include <CGAL/IO/STL.h>

#include <array>
#include <cmath>
#include <cstddef>
#include <iomanip>
#include <iostream>
#include <limits>
#include <optional>
#include <sstream>
#include <string>
#include <vector>

namespace PMP = CGAL::Polygon_mesh_processing;

using Kernel = CGAL::Simple_cartesian<double>;
using Point = Kernel::Point_3;
using Vector = Kernel::Vector_3;
using Mesh = CGAL::Surface_mesh<Point>;
using FaceDescriptor = boost::graph_traits<Mesh>::face_descriptor;
using SideOfMesh = CGAL::Side_of_triangle_mesh<Mesh, Kernel>;

struct WallThicknessEstimate {
  bool valid;
  double min_wall_thickness_mm;
  std::size_t sampled_faces;
  std::size_t sampled_rays;
};

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

static std::string format_number(const double value) {
  std::ostringstream out;
  out << std::fixed << std::setprecision(3) << value;
  return out.str();
}

static Point midpoint(const Point& a, const Point& b) {
  return Point(
    (a.x() + b.x()) * 0.5,
    (a.y() + b.y()) * 0.5,
    (a.z() + b.z()) * 0.5
  );
}

static bool get_face_vertices(const Mesh& mesh, const FaceDescriptor face, std::array<Point, 3>& out_vertices) {
  const auto h0 = halfedge(face, mesh);
  if (h0 == Mesh::null_halfedge()) {
    return false;
  }

  const auto h1 = next(h0, mesh);
  const auto h2 = next(h1, mesh);
  out_vertices[0] = mesh.point(source(h0, mesh));
  out_vertices[1] = mesh.point(source(h1, mesh));
  out_vertices[2] = mesh.point(source(h2, mesh));
  return true;
}

static std::optional<double> trace_thickness_along_direction(
  const SideOfMesh& side_of_mesh,
  const Point& surface_point,
  const Vector& unit_direction,
  const double epsilon,
  const double step_mm,
  const double max_distance_mm) {
  const Point start = surface_point + (unit_direction * epsilon);
  if (side_of_mesh(start) != CGAL::ON_BOUNDED_SIDE) {
    return std::nullopt;
  }

  double previous_mm = 0.0;
  for (double traveled_mm = step_mm; traveled_mm <= max_distance_mm; traveled_mm += step_mm) {
    const Point probe = start + (unit_direction * traveled_mm);
    if (side_of_mesh(probe) == CGAL::ON_UNBOUNDED_SIDE) {
      double low_mm = previous_mm;
      double high_mm = traveled_mm;
      for (int i = 0; i < 12; ++i) {
        const double mid_mm = (low_mm + high_mm) * 0.5;
        const Point mid_probe = start + (unit_direction * mid_mm);
        if (side_of_mesh(mid_probe) == CGAL::ON_UNBOUNDED_SIDE) {
          high_mm = mid_mm;
        } else {
          low_mm = mid_mm;
        }
      }
      return high_mm + epsilon;
    }

    previous_mm = traveled_mm;
  }

  return std::nullopt;
}

static WallThicknessEstimate estimate_min_wall_thickness(
  const Mesh& mesh,
  const double threshold_hint_mm) {
  const SideOfMesh side_of_mesh(mesh);
  const auto bbox = PMP::bbox(mesh);
  const double dx = bbox.xmax() - bbox.xmin();
  const double dy = bbox.ymax() - bbox.ymin();
  const double dz = bbox.zmax() - bbox.zmin();
  const double bbox_diag_mm = std::sqrt((dx * dx) + (dy * dy) + (dz * dz));
  const double max_distance_mm = std::max(2.0, bbox_diag_mm + 1.0);
  const double epsilon = std::max(0.01, std::min(0.05, threshold_hint_mm * 0.05));
  const double step_mm = std::max(0.05, std::min(0.25, threshold_hint_mm * 0.2));

  const std::size_t total_faces = num_faces(mesh);
  const std::size_t max_sample_faces = 2000;
  const std::size_t face_stride = std::max<std::size_t>(1, total_faces / max_sample_faces);

  double min_found_mm = std::numeric_limits<double>::infinity();
  std::size_t sampled_faces = 0;
  std::size_t sampled_rays = 0;
  std::size_t face_index = 0;

  for (const FaceDescriptor face : faces(mesh)) {
    if ((face_index++ % face_stride) != 0) {
      continue;
    }

    std::array<Point, 3> v;
    if (!get_face_vertices(mesh, face, v)) {
      continue;
    }

    const Vector raw_normal = CGAL::cross_product(v[1] - v[0], v[2] - v[0]);
    const double normal_length = std::sqrt(raw_normal.squared_length());
    if (normal_length <= 1e-9) {
      continue;
    }

    const Vector unit_normal = raw_normal / normal_length;
    const Point centroid(
      (v[0].x() + v[1].x() + v[2].x()) / 3.0,
      (v[0].y() + v[1].y() + v[2].y()) / 3.0,
      (v[0].z() + v[1].z() + v[2].z()) / 3.0
    );

    const std::array<Point, 4> samples =
    {
      centroid,
      midpoint(v[0], v[1]),
      midpoint(v[1], v[2]),
      midpoint(v[2], v[0]),
    };

    sampled_faces++;
    for (const Point& sample : samples) {
      const auto forward = trace_thickness_along_direction(
        side_of_mesh,
        sample,
        unit_normal,
        epsilon,
        step_mm,
        max_distance_mm
      );
      if (forward.has_value()) {
        min_found_mm = std::min(min_found_mm, *forward);
        sampled_rays++;
      }

      const auto backward = trace_thickness_along_direction(
        side_of_mesh,
        sample,
        -unit_normal,
        epsilon,
        step_mm,
        max_distance_mm
      );
      if (backward.has_value()) {
        min_found_mm = std::min(min_found_mm, *backward);
        sampled_rays++;
      }
    }

    if (min_found_mm <= std::max(0.05, threshold_hint_mm * 0.2)) {
      break;
    }
  }

  if (!std::isfinite(min_found_mm)) {
    return { false, 0.0, sampled_faces, sampled_rays };
  }

  return { true, min_found_mm, sampled_faces, sampled_rays };
}

int main(int argc, char** argv) {
  if (argc < 2) {
    std::cout << "{\"ok\":false,\"engine\":\"cgal\",\"warnings\":[{\"code\":\"BAD_ARGS\",\"severity\":\"high\",\"message\":\"Expected STL path argument\"}],\"metrics\":{}}\n";
    return 2;
  }

  const std::string stl_path = argv[1];
  double required_min_wall_thickness_mm = 0.0;
  std::string print_process;

  for (int i = 2; i < argc; ++i) {
    const std::string arg = argv[i];
    if (arg == "--min-wall-thickness-mm") {
      if (i + 1 >= argc) {
        std::cout << "{\"ok\":false,\"engine\":\"cgal\",\"warnings\":[{\"code\":\"BAD_ARGS\",\"severity\":\"high\",\"message\":\"Missing value for --min-wall-thickness-mm\"}],\"metrics\":{}}\n";
        return 2;
      }

      try {
        required_min_wall_thickness_mm = std::stod(argv[++i]);
      } catch (...) {
        std::cout << "{\"ok\":false,\"engine\":\"cgal\",\"warnings\":[{\"code\":\"BAD_ARGS\",\"severity\":\"high\",\"message\":\"Invalid --min-wall-thickness-mm value\"}],\"metrics\":{}}\n";
        return 2;
      }
      continue;
    }

    if (arg == "--print-process") {
      if (i + 1 >= argc) {
        std::cout << "{\"ok\":false,\"engine\":\"cgal\",\"warnings\":[{\"code\":\"BAD_ARGS\",\"severity\":\"high\",\"message\":\"Missing value for --print-process\"}],\"metrics\":{}}\n";
        return 2;
      }

      print_process = argv[++i];
      continue;
    }

    std::cout << "{\"ok\":false,\"engine\":\"cgal\",\"warnings\":[{\"code\":\"BAD_ARGS\",\"severity\":\"high\",\"message\":\"Unknown argument: "
              << json_escape(arg)
              << "\"}],\"metrics\":{}}\n";
    return 2;
  }

  if (required_min_wall_thickness_mm < 0.0) {
    std::cout << "{\"ok\":false,\"engine\":\"cgal\",\"warnings\":[{\"code\":\"BAD_ARGS\",\"severity\":\"high\",\"message\":\"--min-wall-thickness-mm must be >= 0\"}],\"metrics\":{}}\n";
    return 2;
  }

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
  const std::size_t components = PMP::connected_components(mesh, fcm);

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

  std::ostringstream metrics;
  metrics << "\"connected_components\":" << components
          << ",\"self_intersections\":" << intersections.size();

  if (required_min_wall_thickness_mm > 0.0) {
    const WallThicknessEstimate thickness = estimate_min_wall_thickness(mesh, required_min_wall_thickness_mm);
    metrics << ",\"required_min_wall_thickness_mm\":" << format_number(required_min_wall_thickness_mm)
            << ",\"estimated_min_wall_thickness_mm\":";

    if (thickness.valid) {
      metrics << format_number(thickness.min_wall_thickness_mm);
    } else {
      metrics << "null";
    }

    metrics << ",\"thickness_sampled_faces\":" << thickness.sampled_faces
            << ",\"thickness_samples\":" << thickness.sampled_rays;

    if (!print_process.empty()) {
      metrics << ",\"print_process\":\"" << json_escape(print_process) << "\"";
    }

    if (!thickness.valid) {
      ok = false;
      push_warning(
        "MIN_WALL_THICKNESS_CHECK_FAILED",
        "high",
        "Automatic minimum wall thickness check could not produce a valid measurement.",
        "Ensure the mesh is closed and manifold, then rerun validation."
      );
    } else if (thickness.min_wall_thickness_mm + 1e-6 < required_min_wall_thickness_mm) {
      ok = false;
      std::ostringstream message;
      message << "Estimated minimum wall thickness is "
              << format_number(thickness.min_wall_thickness_mm)
              << " mm, below required "
              << format_number(required_min_wall_thickness_mm)
              << " mm.";
      push_warning(
        "MIN_WALL_THICKNESS_VIOLATION",
        "high",
        message.str(),
        "Increase thin walls, especially near edges, to satisfy the selected print process threshold."
      );
    }
  }

  std::cout << "{"
            << "\"ok\":" << (ok ? "true" : "false")
            << ",\"engine\":\"cgal\""
            << ",\"warnings\":[" << warnings.str() << "]"
            << ",\"metrics\":{" << metrics.str() << "}"
            << "}\n";

  return 0;
}
