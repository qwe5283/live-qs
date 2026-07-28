export type RouteHandler = (req: Request, url: URL) => Response | Promise<Response>;

interface Route {
  method: string;
  path: string;
  handler: RouteHandler;
}

export class Router {
  private readonly routes: Route[] = [];

  get(path: string, handler: RouteHandler): void {
    this.add("GET", path, handler);
  }

  post(path: string, handler: RouteHandler): void {
    this.add("POST", path, handler);
  }

  delete(path: string, handler: RouteHandler): void {
    this.add("DELETE", path, handler);
  }

  match(method: string, path: string): RouteHandler | null {
    return (
      this.routes.find((route) => {
        if (route.method !== method) return false;
        if (route.path.endsWith("*")) return path.startsWith(route.path.slice(0, -1));
        return route.path === path;
      })?.handler ?? null
    );
  }

  private add(method: string, path: string, handler: RouteHandler): void {
    this.routes.push({ method, path, handler });
  }
}
