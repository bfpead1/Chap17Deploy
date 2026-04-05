import { NavLink } from "react-router-dom";

const links = [
  ["/select-customer", "Select Customer"],
  ["/place-order", "Place Order"],
  ["/admin/orders", "Admin Order History"],
  ["/scoring", "Run Scoring"]
] as const;

type Props = {
  customerLabel: string;
};

export default function Nav({ customerLabel }: Props) {
  return (
    <header className="header">
      <h1>Shop Operations App</h1>
      <p className="banner">Selected customer: {customerLabel}</p>
      <nav>
        {links.map(([to, label]) => (
          <NavLink key={to} to={to} className={({ isActive }) => (isActive ? "nav-link active" : "nav-link")}>
            {label}
          </NavLink>
        ))}
      </nav>
    </header>
  );
}
