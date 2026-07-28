export function cors(response: Response): Response {
  response.headers.set("Access-Control-Allow-Origin", "*");
  response.headers.set("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS");
  response.headers.set("Access-Control-Allow-Headers", "Content-Type, Authorization");
  return response;
}

export function jsonError(message: string, status: number): Response {
  return Response.json({ error: message }, { status });
}

export function notFound(): Response {
  return jsonError("Not found", 404);
}
